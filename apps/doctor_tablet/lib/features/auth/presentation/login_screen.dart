import 'package:doctor_tablet/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../../app/session/session_cubit.dart';

/// Placeholder staff sign-in.
///
/// Deliberately non-functional: no network call, no token handling, no MFA.
/// The only action advances the placeholder session state so the shell can
/// be exercised during development.
final class LoginScreen extends StatelessWidget {
  const LoginScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final strings = AppLocalizations.of(context);
    return Scaffold(
      body: Center(
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 420),
          child: Card(
            margin: const EdgeInsets.all(24),
            child: Padding(
              padding: const EdgeInsets.all(24),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Text(strings.loginTitle, style: Theme.of(context).textTheme.headlineSmall),
                  const SizedBox(height: 8),
                  Text(
                    strings.loginPlaceholderNote,
                    style: Theme.of(context)
                        .textTheme
                        .bodySmall
                        ?.copyWith(color: Theme.of(context).colorScheme.outline),
                  ),
                  const SizedBox(height: 24),
                  const TextField(
                    decoration: InputDecoration(labelText: 'staff id'),
                    enabled: false,
                  ),
                  const SizedBox(height: 16),
                  const TextField(
                    decoration: InputDecoration(labelText: 'password'),
                    obscureText: true,
                    enabled: false,
                  ),
                  const SizedBox(height: 24),
                  FilledButton(
                    onPressed: () => context.read<SessionCubit>().signInPlaceholder(),
                    child: Text(strings.loginContinuePlaceholder),
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}
