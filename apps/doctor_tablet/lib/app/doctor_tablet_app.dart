import 'package:doctor_tablet/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_localizations/flutter_localizations.dart';

import '../features/auth/presentation/login_screen.dart';
import '../features/home/presentation/authenticated_shell.dart';
import '../features/startup/presentation/startup_screen.dart';
import 'app_theme.dart';
import 'localization/locale_cubit.dart';
import 'session/session_cubit.dart';

/// Root widget: wires localization, theme, and the placeholder session flow.
final class DoctorTabletApp extends StatelessWidget {
  const DoctorTabletApp({
    super.key,
    this.initialLocale = defaultLocale,
    this.sessionCubit,
    this.localeCubit,
  });

  final Locale initialLocale;

  // Injectable so widget tests can start from settled states.
  final SessionCubit? sessionCubit;
  final LocaleCubit? localeCubit;

  @override
  Widget build(BuildContext context) {
    return MultiBlocProvider(
      providers: [
        BlocProvider<SessionCubit>(
          create: (_) => sessionCubit ?? (SessionCubit()..completeStartup()),
        ),
        BlocProvider<LocaleCubit>(
          create: (_) => localeCubit ?? (LocaleCubit()..seed(initialLocale)),
        ),
      ],
      child: BlocBuilder<LocaleCubit, Locale>(
        builder: (context, locale) => MaterialApp(
          title: 'Doctor Tablet',
          theme: AppTheme.light(),
          locale: locale,
          supportedLocales: supportedLocales,
          localizationsDelegates: const [
            AppLocalizations.delegate,
            GlobalMaterialLocalizations.delegate,
            GlobalWidgetsLocalizations.delegate,
            GlobalCupertinoLocalizations.delegate,
          ],
          home: const _SessionRouter(),
        ),
      ),
    );
  }
}

/// Maps the session state machine onto full-screen destinations. The first
/// frame always renders the startup screen.
final class _SessionRouter extends StatelessWidget {
  const _SessionRouter();

  @override
  Widget build(BuildContext context) {
    return BlocBuilder<SessionCubit, SessionState>(
      builder: (context, state) => switch (state) {
        SessionStarting() => const StartupScreen(),
        SessionUnauthenticated() => const LoginScreen(),
        SessionAuthenticated() => const AuthenticatedShell(),
      },
    );
  }
}
