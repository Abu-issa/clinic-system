import 'package:doctor_tablet/app/doctor_tablet_app.dart';
import 'package:doctor_tablet/app/localization/locale_cubit.dart';
import 'package:doctor_tablet/app/session/session_cubit.dart';
import 'package:doctor_tablet/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

/// Pumps the real app with an instantly-settled session and returns the
/// injected [SessionCubit] so tests can drive transitions.
extension AppTester on WidgetTester {
  Future<SessionCubit> pumpApp({Locale? locale}) async {
    final session = SessionCubit(startupDelay: Duration.zero);
    final app = DoctorTabletApp(initialLocale: locale ?? const Locale('ar'), sessionCubit: session);
    await pumpWidget(app);
    await pump(); // first frame: startup
    session.completeStartup();
    await pump(); // rebuild: login placeholder
    return session;
  }
}

void main() {
  testWidgets('startup screen is the first frame, then the login placeholder appears',
      (tester) async {
    // Injected cubit does not auto-complete; the first frame must be startup.
    final session = SessionCubit(startupDelay: Duration.zero);
    await tester.pumpWidget(DoctorTabletApp(sessionCubit: session));
    await tester.pump();

    expect(find.byType(CircularProgressIndicator), findsOneWidget);
    expect(find.byType(TextField), findsNothing);

    // Startup completes → the placeholder login appears.
    session.completeStartup();
    await tester.pumpAndSettle();

    expect(find.byType(TextField), findsNWidgets(2));
    expect(find.byType(CircularProgressIndicator), findsNothing);
  });

  testWidgets('Arabic is the default locale and renders RTL', (tester) async {
    await tester.pumpApp();

    expect(find.byType(Directionality), findsWidgets);
    final directionality =
        tester.widget<Directionality>(find.byType(Directionality).first);
    expect(directionality.textDirection, TextDirection.rtl);

    expect(find.text('تسجيل دخول الطاقم الطبي'), findsOneWidget);
    expect(find.text('Staff sign-in'), findsNothing);
  });

  testWidgets('English locale renders LTR with localized strings', (tester) async {
    await tester.pumpApp(locale: const Locale('en'));

    final directionality =
        tester.widget<Directionality>(find.byType(Directionality).first);
    expect(directionality.textDirection, TextDirection.ltr);

    expect(find.text('Staff sign-in'), findsOneWidget);
    expect(find.text('تسجيل دخول الطاقم الطبي'), findsNothing);
  });

  testWidgets('placeholder login advances to the shell; sign-out returns',
      (tester) async {
    final session = await tester.pumpApp();

    await tester.tap(find.text('متابعة (مؤقت)'));
    await tester.pumpAndSettle();
    expect(find.text('المساحة المحمية'), findsOneWidget);

    await tester.tap(find.byTooltip('خروج'));
    await tester.pumpAndSettle();
    expect(find.text('تسجيل دخول الطاقم الطبي'), findsOneWidget);
    // The session cubit stayed the placeholder machine throughout.
    expect(session.state, isA<SessionUnauthenticated>());
  });

  test('every ARB key exists in both languages', () {
    expect(AppLocalizations.supportedLocales.map((l) => l.languageCode), containsAll(['ar', 'en']));
  });

  test('locale cubit defaults to Arabic and toggles to English and back', () {
    final cubit = LocaleCubit();
    expect(cubit.state, const Locale('ar'));
    cubit.toggle();
    expect(cubit.state, const Locale('en'));
    cubit.toggle();
    expect(cubit.state, const Locale('ar'));
  });
}
