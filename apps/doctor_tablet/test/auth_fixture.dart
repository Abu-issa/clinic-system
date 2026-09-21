import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:clinic_core/clinic_core.dart';
import 'package:dio/dio.dart';
import 'package:doctor_tablet/app/session/session_cubit.dart';
import 'package:doctor_tablet/core/api/api_client_factory.dart';
import 'package:doctor_tablet/features/auth/data/mobile_auth.dart';
import 'package:doctor_tablet/features/auth/data/token_store.dart';

final class MemoryTokens implements TokenStore {
  String? value;
  int writes = 0;
  bool failWrite = false;
  Completer<void>? writeGate;
  @override
  Future<String?> read() async => value;
  @override
  Future<void> write(String bundle) async {
    await writeGate?.future;
    if (failWrite) throw StateError('Storage unavailable');
    writes++;
    value = bundle;
  }

  @override
  Future<void> clear() async {
    value = null;
  }
}

final class FakeClinic implements HttpClientAdapter {
  int version = 0;
  int refreshes = 0;
  int sessionCalls = 0;
  int logouts = 0;
  int oldRequests = 0;
  bool invalidPassword = false;
  bool invalidMfa = false;
  bool expiredAccess = false;
  bool rejectRefresh = false;
  bool always401 = false;
  bool offlineLogout = false;
  bool refreshTimeout = false;
  Completer<void>? refreshGate;
  Completer<void>? lateGate;
  final refreshStarted = Completer<void>();
  final twoOldRequests = Completer<void>();
  void Function()? onLogout;
  Future<ResponseBody> Function(RequestOptions)? patientSearch;
  Future<ResponseBody> Function(RequestOptions)? notebook;
  List<String> roles = ['Doctor'];
  final List<List<int>> multipartBodies = [];
  String get access => String.fromCharCode(65 + version) * 43;
  String get refresh => String.fromCharCode(97 + version) * 43;
  Map<String, dynamic> get tokens => {
    'accessToken': access,
    'refreshToken': refresh,
    'tokenType': 'Bearer',
    'expiresIn': 900,
    'expiresAtUtc': '2030-01-01T00:15:00Z',
    'refreshExpiresAtUtc': '2030-01-08T00:00:00Z',
  };
  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    final path = options.path;
    if (options.data is FormData && requestStream != null) {
      multipartBodies.add(
        await requestStream.expand((chunk) => chunk).toList(),
      );
    }
    if (path.endsWith('/login')) {
      return reply(invalidPassword ? 401 : 200, {
        'next': 'totp',
        'challenge': 'private-challenge',
      });
    }
    if (path.endsWith('/mfa')) return reply(invalidMfa ? 401 : 200, tokens);
    if (path.endsWith('/refresh')) {
      refreshes++;
      if (!refreshStarted.isCompleted) refreshStarted.complete();
      await refreshGate?.future;
      if (refreshTimeout) {
        throw DioException(
          requestOptions: options,
          type: DioExceptionType.receiveTimeout,
        );
      }
      if (rejectRefresh || (options.data as Map)['refreshToken'] != refresh) {
        return reply(401, {});
      }
      version++;
      expiredAccess = false;
      return reply(200, tokens);
    }
    if (path.endsWith('/logout')) {
      logouts++;
      onLogout?.call();
      if (offlineLogout) {
        throw DioException(
          requestOptions: options,
          type: DioExceptionType.connectionError,
        );
      }
      if (expiredAccess) return reply(401, {});
      return reply(204, {});
    }
    sessionCalls++;
    if (path == '/late' && options.extra['clinic.auth.retry'] != true) {
      await lateGate?.future;
      return reply(401, {});
    }
    if (always401 ||
        expiredAccess ||
        options.headers['Authorization'] != 'Bearer $access') {
      oldRequests++;
      if (oldRequests >= 2 && !twoOldRequests.isCompleted) {
        twoOldRequests.complete();
      }
      return reply(401, {});
    }
    if (path == '/api/mobile/staff/patients/search' && patientSearch != null) {
      return patientSearch!(options);
    }
    if (path.contains('/notebook/pages')) {
      if (notebook != null) return notebook!(options);
      return reply(200, {
        'items': [],
        'page': 1,
        'pageSize': 10,
        'hasMore': false,
      });
    }
    return reply(200, {'staffId': 'staff-1', 'roles': roles});
  }

  ResponseBody reply(int code, Map<String, dynamic> body) =>
      ResponseBody.fromString(
        jsonEncode(body),
        code,
        headers: {
          Headers.contentTypeHeader: ['application/json'],
        },
      );
  @override
  void close({bool force = false}) {}
}

final class AuthFixture {
  AuthFixture() {
    final factory = ApiClientFactory(
      ApiConfig.production('https://clinic.test'),
    );
    final transport = factory.create()..httpClientAdapter = backend;
    api = factory.create()..httpClientAdapter = backend;
    auth = MobileAuth(transport: transport, api: api, store: store);
    cubit = SessionCubit(auth);
  }
  final backend = FakeClinic();
  final store = MemoryTokens();
  late final Dio api;
  late final MobileAuth auth;
  late final SessionCubit cubit;
  Future<void> signIn() async {
    await cubit.completeStartup();
    await cubit.login('staff', 'test-password');
    await cubit.verify('123456');
  }
}
