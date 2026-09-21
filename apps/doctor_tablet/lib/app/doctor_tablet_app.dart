import 'package:doctor_tablet/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_localizations/flutter_localizations.dart';

import '../features/auth/presentation/login_screen.dart';
import '../features/auth/presentation/mfa_screen.dart';
import '../features/patients/data/patient_search.dart';
import '../features/notebook/data/notebook_api.dart';
import '../features/notebook/state/notebook_cubit.dart';
import '../features/patients/state/patient_context_cubit.dart';
import '../features/home/presentation/authenticated_shell.dart';
import '../features/startup/presentation/startup_screen.dart';
import 'app_theme.dart';
import 'localization/locale_cubit.dart';
import 'session/session_cubit.dart';

/// Root widget: wires localization, theme, and validated staff authentication.
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
          create: (_) =>
              sessionCubit ?? (SessionCubit.configured()..completeStartup()),
        ),
        BlocProvider<LocaleCubit>(
          create: (_) => localeCubit ?? (LocaleCubit()..seed(initialLocale)),
        ),
        BlocProvider<PatientContextCubit>(
          lazy: false,
          create: (context) {
            final session = context.read<SessionCubit>();
            return PatientContextCubit(
              session,
              session.auth == null ? null : PatientSearch(session.auth!.api),
            );
          },
        ),
        BlocProvider<NotebookCubit>(
          lazy: false,
          create: (context) {
            final session = context.read<SessionCubit>();
            return NotebookCubit(
              session,
              context.read<PatientContextCubit>(),
              session.auth == null ? null : NotebookApi(session.auth!.api),
            );
          },
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
        SessionUnauthenticated() ||
        SessionExpired() ||
        SessionPasswordLoading() => const LoginScreen(),
        SessionMfaRequired() => const MfaScreen(),
        SessionRefreshing(:final staff) =>
          staff == null ? const StartupScreen() : const AuthenticatedShell(),
        SessionLoggingOut() => const StartupScreen(),
        SessionAuthenticated() => const AuthenticatedShell(),
      },
    );
  }
}
