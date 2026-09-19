import 'package:doctor_tablet/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../../app/localization/locale_cubit.dart';
import '../../../app/session/session_cubit.dart';

/// Placeholder authenticated workspace.
///
/// No patient, notebook, or clinical content: later phases build the real
/// feature destinations. Includes the language toggle so RTL/LTR behavior
/// can be exercised on-device from the first runnable slice.
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
            onPressed: () => context.read<SessionCubit>().signOutPlaceholder(),
          ),
        ],
      ),
      body: Center(
        child: Padding(
          padding: const EdgeInsets.all(24),
          child: Text(
            strings.shellPlaceholderNote,
            textAlign: TextAlign.center,
            style: Theme.of(context).textTheme.bodyLarge,
          ),
        ),
      ),
    );
  }
}
