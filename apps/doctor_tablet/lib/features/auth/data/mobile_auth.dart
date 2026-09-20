import 'dart:async';
import 'dart:convert';

import 'package:dio/dio.dart';

import 'token_store.dart';

enum AuthIssue {
  credentials,
  mfa,
  enrollment,
  network,
  expired,
  storage,
  configuration,
}

enum AuthEvent { refreshing, refreshed, expired }

final class AuthFailure implements Exception {
  const AuthFailure(this.issue);
  final AuthIssue issue;
  @override
  String toString() => 'Authentication failed (${issue.name}).';
}

final class StaffSession {
  StaffSession.fromJson(Map<String, dynamic> json)
    : staffId = json['staffId'] as String,
      roles = List<String>.unmodifiable(json['roles'] as List) {
    if (staffId.isEmpty ||
        !roles.any(
          const ['Doctor', 'DoctorAssistant', 'Receptionist'].contains,
        )) {
      throw const FormatException('Invalid session response.');
    }
  }
  final String staffId;
  final List<String> roles;
}

/// Own one instance per app/isolate. Credentials never enter Cubit states.
final class MobileAuth {
  MobileAuth({
    required this.transport,
    required this.api,
    required this.store,
  }) {
    final uri = Uri.tryParse(transport.options.baseUrl);
    if (uri == null ||
        uri.scheme != 'https' ||
        uri.host.isEmpty ||
        uri.userInfo.isNotEmpty ||
        uri.hasQuery ||
        uri.hasFragment ||
        (uri.path.isNotEmpty && uri.path != '/') ||
        api.options.baseUrl != transport.options.baseUrl) {
      throw const AuthFailure(AuthIssue.configuration);
    }
    api.interceptors.add(_AuthInterceptor(this));
  }

  static const route = '/api/mobile/staff/auth/';
  final Dio transport; // No auth/retry/logging interceptors on this client.
  final Dio api;
  final TokenStore store;
  final _events = StreamController<AuthEvent>.broadcast(sync: true);
  Stream<AuthEvent> get events => _events.stream;
  TokenPair? _tokens;
  String? _challenge;
  int _epoch = 0;
  bool _ending = false;
  Future<bool>? _refreshFlight;
  Future<void> _storageTail = Future<void>.value();

  Future<void> _storage(Future<void> Function() action) {
    final operation = _storageTail.then((_) => action());
    _storageTail = operation.catchError((Object _) {});
    return operation;
  }

  Future<void> _save(TokenPair pair, int epoch) async {
    await _storage(() async {
      if (epoch != _epoch) throw const AuthFailure(AuthIssue.expired);
      try {
        await store.write(pair.encode(transport.options.baseUrl));
      } catch (_) {
        throw const AuthFailure(AuthIssue.storage);
      }
      if (epoch != _epoch) throw const AuthFailure(AuthIssue.expired);
      _tokens =
          pair; // Publish only after secure storage acknowledges the pair.
    });
  }

  Future<void> _clear({bool expired = false}) async {
    _epoch++;
    _tokens = null;
    _challenge = null;
    try {
      await _storage(store.clear);
    } catch (_) {
      throw const AuthFailure(AuthIssue.storage);
    } finally {
      if (expired) _events.add(AuthEvent.expired);
    }
  }

  Future<StaffSession?> restore() async {
    try {
      final value = await store.read();
      if (value == null) return null;
      final json = jsonDecode(value) as Map<String, dynamic>;
      if (json['origin'] != transport.options.baseUrl) {
        throw const AuthFailure(AuthIssue.expired);
      }
      _tokens = TokenPair.fromJson(json);
      return await validateSession();
    } catch (_) {
      await _clear(expired: true);
      throw const AuthFailure(AuthIssue.expired);
    }
  }

  Future<void> login(String login, String password) async {
    await _clear();
    final epoch = _epoch;
    try {
      final response = await transport.post<Map<String, dynamic>>(
        '${route}login',
        data: {'login': login.trim(), 'password': password},
      );
      if (epoch != _epoch) throw const AuthFailure(AuthIssue.expired);
      final json = response.data!;
      if (json['next'] != 'totp' || json['challenge'] is! String) {
        throw const AuthFailure(AuthIssue.credentials);
      }
      _challenge = json['challenge'] as String;
    } on DioException catch (e) {
      if (e.response?.statusCode == 403) {
        throw const AuthFailure(AuthIssue.enrollment);
      }
      throw AuthFailure(
        e.response?.statusCode == 401
            ? AuthIssue.credentials
            : AuthIssue.network,
      );
    }
  }

  void cancelChallenge() {
    _epoch++;
    _challenge = null;
  }

  Future<StaffSession> verify(String code) async {
    final challenge = _challenge;
    final epoch = _epoch;
    if (challenge == null) throw const AuthFailure(AuthIssue.expired);
    late final Response<Map<String, dynamic>> response;
    try {
      response = await transport.post<Map<String, dynamic>>(
        '${route}mfa',
        data: {'challenge': challenge, 'code': code},
      );
    } on DioException catch (e) {
      throw AuthFailure(
        e.response?.statusCode == 401 ? AuthIssue.mfa : AuthIssue.network,
      );
    }
    try {
      final pair = TokenPair.fromJson(response.data!);
      await _save(pair, epoch);
      _challenge = null;
      return await validateSession();
    } catch (e) {
      if (epoch == _epoch) await _clear(expired: true);
      throw AuthFailure(
        e is AuthFailure && e.issue == AuthIssue.storage
            ? AuthIssue.storage
            : AuthIssue.expired,
      );
    }
  }

  Future<StaffSession> validateSession() async {
    final epoch = _epoch;
    final response = await api.get<Map<String, dynamic>>('${route}session');
    if (_tokens == null || epoch != _epoch) {
      throw const AuthFailure(AuthIssue.expired);
    }
    return StaffSession.fromJson(response.data!);
  }

  Future<bool> _recover(String access, int epoch, {bool logout = false}) async {
    if (epoch != _epoch || _tokens == null || (_ending && !logout)) {
      return false;
    }
    // Late 401s from requests sent before a completed rotation reuse the new pair.
    if (_tokens!.access != access) return true;
    return _refreshFlight ??= _rotate(epoch)
        .whenComplete(() => _refreshFlight = null);
  }

  Future<bool> _rotate(int epoch) async {
    final current = _tokens!;
    _events.add(AuthEvent.refreshing);
    try {
      final response = await transport.post<Map<String, dynamic>>(
        '${route}refresh',
        data: {'refreshToken': current.refresh},
      );
      await _save(TokenPair.fromJson(response.data!), epoch);
      _events.add(AuthEvent.refreshed);
      return true;
    } catch (_) {
      // An ambiguous timeout can mean rotation committed. Never retry a refresh.
      if (epoch == _epoch) {
        try {
          await _clear(expired: true);
        } catch (_) {
          /* Memory is already cleared. */
        }
      }
      return false;
    }
  }

  Future<void> logout() async {
    if (_ending) return;
    _ending = true;
    _challenge = null;
    try {
      await _refreshFlight;
      var pair = _tokens;
      if (pair != null) {
        try {
          await _remoteLogout(pair.access);
        } on DioException catch (e) {
          if (e.response?.statusCode == 401 &&
              await _recover(pair.access, _epoch, logout: true)) {
            pair = _tokens;
            if (pair != null) await _remoteLogout(pair.access);
          }
        }
      }
    } catch (_) {
      /* Offline logout still clears local credentials. */
    } finally {
      try {
        await _clear();
      } finally {
        _ending = false;
      }
    }
  }

  Future<void> _remoteLogout(String access) async {
    await transport.post<void>(
      '${route}logout',
      options: Options(headers: {'Authorization': 'Bearer $access'}),
    );
  }

  Future<void> dispose() async {
    _epoch++;
    _tokens = null;
    transport.close(force: true);
    api.close(force: true);
    await _events.close();
  }
}

final class _AuthInterceptor extends Interceptor {
  _AuthInterceptor(this.auth);
  final MobileAuth auth;
  static const epochKey = 'clinic.auth.epoch';
  static const retryKey = 'clinic.auth.retry';

  @override
  void onRequest(RequestOptions options, RequestInterceptorHandler handler) {
    final target = options.uri;
    final origin = Uri.parse(auth.transport.options.baseUrl);
    if (target.origin != origin.origin ||
        target.scheme != 'https' ||
        target.userInfo.isNotEmpty ||
        auth._tokens == null ||
        auth._ending ||
        (options.extra.containsKey(epochKey) &&
            options.extra[epochKey] != auth._epoch)) {
      handler.reject(
        DioException(
          requestOptions: options,
          message: 'Authentication required.',
        ),
      );
      return;
    }
    options.headers['Authorization'] = 'Bearer ${auth._tokens!.access}';
    options.extra[epochKey] = auth._epoch;
    handler.next(options);
  }

  @override
  void onError(DioException error, ErrorInterceptorHandler handler) async {
    final request = error.requestOptions;
    if (error.response?.statusCode != 401) {
      handler.next(error);
      return;
    }
    final epoch = request.extra[epochKey];
    if (epoch != auth._epoch) {
      handler.next(error);
      return;
    }
    if (request.extra[retryKey] == true) {
      try {
        await auth._clear(expired: true);
      } catch (_) {
        /* Fail closed. */
      }
      handler.next(error);
      return;
    }
    final header = request.headers['Authorization'] as String?;
    if (header == null ||
        epoch is! int ||
        !await auth._recover(header.substring(7), epoch)) {
      handler.next(error);
      return;
    }
    try {
      // A new request option object prevents retry flags leaking to other calls.
      final response = await auth.api.fetch<dynamic>(
        request.copyWith(extra: {...request.extra, retryKey: true}),
      );
      handler.resolve(response);
    } on DioException catch (e) {
      handler.next(e);
    }
  }
}
