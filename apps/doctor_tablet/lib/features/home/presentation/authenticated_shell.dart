import 'package:doctor_tablet/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter/foundation.dart';

import '../../notebook/presentation/stylus_diagnostics.dart';

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
          if (kDebugMode)
            IconButton(
              key: const Key('stylus-diagnostics'),
              tooltip: 'DEV · Stylus diagnostics',
              icon: const Icon(Icons.bug_report),
              onPressed: () => Navigator.of(context).push(
                MaterialPageRoute<void>(
                  builder: (_) => const StylusDiagnostics(),
                ),
              ),
            ),
          TextButton(
            onPressed: () => context.read<LocaleCubit>().toggle(),
            child: Text(strings.languageToggle),
          ),
          IconButton(
            tooltip: strings.shellSignOut,
            icon: const Icon(Icons.logout),
            onPressed: () async {
              final success = await context.read<SessionCubit>().logout();
              if (!success && context.mounted) {
                ScaffoldMessenger.of(context).showSnackBar(
                  SnackBar(content: Text(strings.inkLogoutBlocked)),
                );
              }
            },
          ),
        ],
      ),
      body: const PatientWorkspace(),
    );
  }
}
