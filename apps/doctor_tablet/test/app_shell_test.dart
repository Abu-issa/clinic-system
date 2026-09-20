import 'dart:convert';
import 'dart:io';

import 'package:doctor_tablet/app/doctor_tablet_app.dart';
import 'package:doctor_tablet/app/localization/locale_cubit.dart';
import 'package:doctor_tablet/app/session/session_cubit.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'auth_fixture.dart';

void main() {
  testWidgets(
    'startup remains protected until storage and session checks finish',
    (tester) async {
      final f = AuthFixture();
      await tester.pumpWidget(DoctorTabletApp(sessionCubit: f.cubit));
      expect(find.byType(CircularProgressIndicator), findsOneWidget);
      await f.cubit.completeStartup();
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('login')), findsOneWidget);
      expect(find.byType(CircularProgressIndicator), findsNothing);
    },
  );

  for (final language in ['ar', 'en']) {
    testWidgets('$language password and MFA UI reaches shell then logs out', (
      tester,
    ) async {
      final f = AuthFixture();
      await tester.pumpWidget(
        DoctorTabletApp(initialLocale: Locale(language), sessionCubit: f.cubit),
      );
      await f.cubit.completeStartup();
      await tester.pumpAndSettle();
      final arabic = language == 'ar';
      expect(
        find.text(arabic ? 'تسجيل دخول الطاقم الطبي' : 'Staff sign-in'),
        findsOneWidget,
      );
      expect(
        Directionality.of(tester.element(find.byKey(const Key('login')))),
        arabic ? TextDirection.rtl : TextDirection.ltr,
      );
      await tester.enterText(
        find.byKey(const Key('login')),
        'doctor@example.test',
      );
      await tester.enterText(
        find.byKey(const Key('password')),
        'test-password',
      );
      await tester.tap(find.byKey(const Key('sign-in')));
      await tester.pumpAndSettle();
      expect(
        find.text(arabic ? 'التحقق بخطوتين' : 'Two-step verification'),
        findsOneWidget,
      );
      expect(f.cubit.state, isA<SessionMfaRequired>());
      await tester.enterText(find.byKey(const Key('mfa-code')), '123456');
      await tester.tap(find.byKey(const Key('verify-mfa')));
      await tester.pumpAndSettle();
      expect(
        find.text(arabic ? 'المساحة المحمية' : 'Protected workspace'),
        findsOneWidget,
      );
      await tester.tap(find.byTooltip(arabic ? 'خروج' : 'Sign out'));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('login')), findsOneWidget);
      expect(f.store.value, isNull);
    });
  }

  testWidgets(
    'Arabic default, language toggle works on login and MFA; errors localized',
    (tester) async {
      final f = AuthFixture();
      f.backend.invalidPassword = true;
      await tester.pumpWidget(DoctorTabletApp(sessionCubit: f.cubit));
      await f.cubit.completeStartup();
      await tester.pumpAndSettle();
      expect(find.text('تسجيل دخول الطاقم الطبي'), findsOneWidget);
      await tester.enterText(find.byKey(const Key('login')), 'staff');
      await tester.enterText(find.byKey(const Key('password')), 'bad-password');
      await tester.tap(find.byKey(const Key('sign-in')));
      await tester.pumpAndSettle();
      expect(
        find.text('تعذّر تسجيل الدخول. تحقق من بياناتك وحاول مجددًا.'),
        findsOneWidget,
      );
      await tester.tap(find.text('English'));
      await tester.pumpAndSettle();
      expect(
        find.text('Unable to sign in. Check your credentials and try again.'),
        findsOneWidget,
      );
      f.backend.invalidPassword = false;
      await tester.enterText(
        find.byKey(const Key('password')),
        'test-password',
      );
      await tester.tap(find.byKey(const Key('sign-in')));
      await tester.pumpAndSettle();
      f.backend.invalidMfa = true;
      await tester.enterText(find.byKey(const Key('mfa-code')), '000000');
      await tester.tap(find.byKey(const Key('verify-mfa')));
      await tester.pumpAndSettle();
      expect(
        find.text(
          'The code or challenge is invalid. Try again or restart sign-in.',
        ),
        findsOneWidget,
      );
      await tester.tap(find.text('العربية'));
      await tester.pumpAndSettle();
      expect(
        find.text(
          'الرمز أو طلب التحقق غير صالح. حاول مجددًا أو أعد تسجيل الدخول.',
        ),
        findsOneWidget,
      );
      await tester.tap(find.text('العودة لتسجيل الدخول'));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('login')), findsOneWidget);
    },
  );

  testWidgets('refresh failure routes authenticated UI back to login', (
    tester,
  ) async {
    final f = AuthFixture();
    final signIn = f.signIn();
    await tester.pumpAndSettle();
    await signIn;
    await tester.pumpWidget(DoctorTabletApp(sessionCubit: f.cubit));
    await tester.pumpAndSettle();
    f.backend.expiredAccess = true;
    f.backend.rejectRefresh = true;
    // Run the real interceptor; the UI reacts to its session-expired event.
    final failure = expectLater(
      f.api.get<dynamic>('/protected'),
      throwsA(isA<Exception>()),
    );
    await tester.pumpAndSettle();
    await failure;
    expect(find.byKey(const Key('login')), findsOneWidget);
    expect(find.text('انتهت جلستك. يرجى تسجيل الدخول مجددًا.'), findsOneWidget);
  });

  test('ARB key coverage matches and locale defaults to Arabic', () async {
    Set<String> keys(String language) => (jsonDecode(
      File('lib/l10n/app_$language.arb').readAsStringSync(),
    ) as Map).keys.cast<String>().where((key) => !key.startsWith('@')).toSet();
    expect(keys('ar'), keys('en'));
    final cubit = LocaleCubit();
    expect(cubit.state, const Locale('ar'));
    cubit.toggle();
    expect(cubit.state, const Locale('en'));
    cubit.toggle();
    expect(cubit.state, const Locale('ar'));
    await cubit.close();
  });
}
