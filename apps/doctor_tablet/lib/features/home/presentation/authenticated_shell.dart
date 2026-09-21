import 'package:doctor_tablet/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../../app/localization/locale_cubit.dart';
import '../../../app/session/session_cubit.dart';
import '../../patients/presentation/patient_workspace.dart';

/// Authenticated patient workspace with a persistent patient header.
final class AuthenticatedShell extends StatelessWidget {
  const AuthenticatedShell({super.key});

  @override
  Widget build(BuildContext context) {
    final strings = AppLocalizations.of(context);
    return Scaffold(
      appBar: AppBar(
        title: Text(strings.shellTitle),
        actions: [
          TextButton(
            onPressed: () => context.read<LocaleCubit>().toggle(),
            child: Text(strings.languageToggle),
          ),
          IconButton(
            tooltip: strings.shellSignOut,
            icon: const Icon(Icons.logout),
            onPressed: () => context.read<SessionCubit>().logout(),
          ),
        ],
      ),
      body: const PatientWorkspace(),
    );
  }
}
