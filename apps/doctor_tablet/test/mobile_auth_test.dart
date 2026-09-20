import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:dio/dio.dart';
import 'package:doctor_tablet/app/session/session_cubit.dart';
import 'package:doctor_tablet/features/auth/data/mobile_auth.dart';
import 'package:doctor_tablet/features/auth/data/token_store.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';

import 'auth_fixture.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  late AuthFixture f;
  setUp(() {
    f = AuthFixture();
  });
  tearDown(() async {
    await f.cubit.close();
  });

  test('password then MFA validates session before authenticated', () async {
    final states = <SessionState>[];
    final subscription = f.cubit.stream.listen(states.add);
    await f.cubit.completeStartup();
    await f.cubit.login('staff', 'test-password');
    expect(f.cubit.state, isA<SessionMfaRequired>());
    expect(f.store.value, isNull);
    await f.cubit.verify('123456');
    expect(f.cubit.state, isA<SessionAuthenticated>());
    expect(f.backend.sessionCalls, 1);
    expect(states.whereType<SessionPasswordLoading>(), hasLength(1));
    expect(f.store.writes, 1);
    await subscription.cancel();
  });

  test('invalid credentials cannot enter MFA or save credentials', () async {
    f.backend.invalidPassword = true;
    await f.cubit.completeStartup();
    await f.cubit.login('staff', 'bad-password');
    expect(
      (f.cubit.state as SessionUnauthenticated).issue,
      AuthIssue.credentials,
    );
    expect(f.store.value, isNull);
  });

  test('invalid MFA remains in MFA screen and may retry', () async {
    await f.cubit.completeStartup();
    await f.cubit.login('staff', 'test-password');
    f.backend.invalidMfa = true;
    await f.cubit.verify('000000');
    expect((f.cubit.state as SessionMfaRequired).issue, AuthIssue.mfa);
    expect(f.store.value, isNull);
    f.backend.invalidMfa = false;
    await f.cubit.verify('123456');
    expect(f.cubit.state, isA<SessionAuthenticated>());
  });

  test('startup reads stored pair and calls session', () async {
    f.store.value = TokenPair.fromJson(f.backend.tokens)
        .encode('https://clinic.test');
    await f.cubit.completeStartup();
    expect(f.cubit.state, isA<SessionAuthenticated>());
    expect(f.backend.sessionCalls, 1);
  });

  test(
    'startup refreshes expired access then validates restored session',
    () async {
      f.store.value = TokenPair.fromJson(f.backend.tokens)
          .encode('https://clinic.test');
      f.backend.expiredAccess = true;
      await f.cubit.completeStartup();
      expect(f.cubit.state, isA<SessionAuthenticated>());
      expect(f.backend.refreshes, 1);
      expect(f.backend.sessionCalls, 2);
    },
  );

  test('corrupt or wrong-origin stored credentials fail closed', () async {
    f.store.value = TokenPair.fromJson(f.backend.tokens)
        .encode('https://another-clinic.test');
    await f.cubit.completeStartup();
    expect(f.cubit.state, isA<SessionExpired>());
    expect(f.store.value, isNull);
    expect(f.backend.sessionCalls, 0);
  });

  test('401 rotates pair and retries original request exactly once', () async {
    await f.signIn();
    f.backend.expiredAccess = true;
    final response = await f.api.get<dynamic>('/protected');
    expect(response.statusCode, 200);
    expect(f.backend.refreshes, 1);
    expect(f.backend.sessionCalls, 3);
    final saved = jsonDecode(f.store.value!) as Map;
    expect(saved['accessToken'], f.backend.access);
    expect(saved['refreshToken'], f.backend.refresh);
  });

  test(
    'simultaneous 401s share one refresh including delayed old response',
    () async {
      await f.signIn();
      f.backend.expiredAccess = true;
      f.backend.refreshGate = Completer<void>();
      f.backend.lateGate = Completer<void>();
      final late = f.api.get<dynamic>('/late');
      final first = f.api.get<dynamic>('/first');
      final second = f.api.get<dynamic>('/second');
      await f.backend.refreshStarted.future;
      await f.backend.twoOldRequests.future;
      expect(f.cubit.state, isA<SessionRefreshing>());
      expect(f.backend.refreshes, 1);
      f.backend.refreshGate!.complete();
      await Future.wait([first, second]);
      f.backend.lateGate!.complete();
      expect((await late).statusCode, 200);
      expect(f.backend.refreshes, 1);
    },
  );

  for (final timeout in [false, true]) {
    test(
      'refresh ${timeout ? 'timeout' : 'rejection'} clears credentials without retry storms',
      () async {
        await f.signIn();
        f.backend.expiredAccess = true;
        f.backend.rejectRefresh = !timeout;
        f.backend.refreshTimeout = timeout;
        await expectLater(
          f.api.get<dynamic>('/protected'),
          throwsA(isA<DioException>()),
        );
        expect(f.cubit.state, isA<SessionExpired>());
        expect(f.store.value, isNull);
        await expectLater(
          f.api.get<dynamic>('/again'),
          throwsA(isA<DioException>()),
        );
        expect(f.backend.refreshes, 1);
      },
    );
  }

  test('401 on retried request clears session and cannot loop', () async {
    await f.signIn();
    f.backend.always401 = true;
    await expectLater(
      f.api.get<dynamic>('/protected'),
      throwsA(isA<DioException>()),
    );
    expect(f.backend.refreshes, 1);
    expect(f.cubit.state, isA<SessionExpired>());
    expect(f.store.value, isNull);
  });

  test(
    'rotation publishes one pair only after secure write completes',
    () async {
      await f.signIn();
      final oldBundle = f.store.value;
      f.backend.expiredAccess = true;
      f.store.writeGate = Completer<void>();
      final request = f.api.get<dynamic>('/protected');
      await f.backend.refreshStarted.future;
      expect(f.store.value, oldBundle);
      expect(f.cubit.state, isA<SessionRefreshing>());
      f.store.writeGate!.complete();
      await request;
      final json = jsonDecode(f.store.value!) as Map;
      expect(json['accessToken'], f.backend.access);
      expect(json['refreshToken'], f.backend.refresh);
      expect(f.store.writes, 2);
    },
  );

  test('storage failure after rotation clears credentials', () async {
    await f.signIn();
    f.backend.expiredAccess = true;
    f.store.failWrite = true;
    await expectLater(
      f.api.get<dynamic>('/protected'),
      throwsA(isA<DioException>()),
    );
    expect(f.store.value, isNull);
    expect(f.cubit.state, isA<SessionExpired>());
  });

  for (final offline in [false, true]) {
    test('logout attempts backend before cleanup, offline=$offline', () async {
      await f.signIn();
      f.backend.offlineLogout = offline;
      f.backend.onLogout = () => expect(f.store.value, isNotNull);
      await f.cubit.logout();
      expect(f.backend.logouts, 1);
      expect(f.store.value, isNull);
      expect(f.cubit.state, isA<SessionUnauthenticated>());
    });
  }

  test(
    'logout with expired access refreshes once then revokes latest session',
    () async {
      await f.signIn();
      f.backend.expiredAccess = true;
      await f.cubit.logout();
      expect(f.backend.refreshes, 1);
      expect(f.backend.logouts, 2);
      expect(f.store.value, isNull);
    },
  );

  test('logout during refresh waits then clears the rotated pair', () async {
    await f.signIn();
    f.backend.expiredAccess = true;
    f.backend.refreshGate = Completer<void>();
    final request = f.api.get<dynamic>('/protected');
    final rejected = expectLater(request, throwsA(isA<DioException>()));
    await f.backend.refreshStarted.future;
    final logout = f.cubit.logout();
    f.backend.refreshGate!.complete();
    await logout;
    await rejected;
    expect(f.store.value, isNull);
    expect(f.cubit.state, isA<SessionUnauthenticated>());
    expect(f.backend.refreshes, 1);
    expect(f.backend.logouts, 1);
  });

  test(
    'protected client refuses a different origin before sending credentials',
    () async {
      await f.signIn();
      final count = f.backend.sessionCalls;
      await expectLater(
        f.api.get<dynamic>('https://evil.test/session'),
        throwsA(isA<DioException>()),
      );
      expect(f.backend.sessionCalls, count);
      expect(f.api.options.followRedirects, isFalse);
    },
  );

  test('production storage writes one secure-channel item and never SharedPreferences', () async {
    const channel = MethodChannel(
      'plugins.it_nomads.com/flutter_secure_storage',
    );
    final calls = <MethodCall>[];
    String? saved;
    final messenger =
        TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger;
    messenger.setMockMethodCallHandler(channel, (call) async {
      calls.add(call);
      final args = call.arguments as Map;
      switch (call.method) {
        case 'write':
          saved = args['value'] as String;
          return null;
        case 'read':
          return saved;
        case 'delete':
          saved = null;
          return null;
      }
      return null;
    });
    addTearDown(() => messenger.setMockMethodCallHandler(channel, null));
    const store = SecureTokenStore();
    final pair = TokenPair.fromJson(f.backend.tokens)
        .encode('https://clinic.test');
    await store.write(pair);
    expect(await store.read(), pair);
    expect(calls.where((c) => c.method == 'write'), hasLength(1));
    expect((calls.first.arguments as Map)['key'], SecureTokenStore.key);
    await store.clear();
    expect(saved, isNull);
    final source = Directory('lib')
        .listSync(recursive: true)
        .whereType<File>()
        .where((file) => file.path.endsWith('.dart'))
        .map((file) => file.readAsStringSync())
        .join();
    expect(source, isNot(contains('SharedPreferences')));
    expect(source, isNot(contains('LogInterceptor(')));
  });

  test('auth flow emits no secret logs', () async {
    final logs = <String>[];
    await runZoned(
      () async {
        await f.signIn();
        f.backend.expiredAccess = true;
        await f.api.get<dynamic>('/protected');
        await f.cubit.logout();
      },
      zoneSpecification: ZoneSpecification(
        print: (self, parent, zone, line) => logs.add(line),
      ),
    );
    expect(logs, isEmpty);
    expect(
      TokenPair.fromJson(f.backend.tokens).toString(),
      'TokenPair(redacted)',
    );
  });
}
