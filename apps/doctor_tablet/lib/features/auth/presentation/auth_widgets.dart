import 'package:doctor_tablet/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../../app/localization/locale_cubit.dart';
import '../data/mobile_auth.dart';

final class AuthFrame extends StatelessWidget {
  const AuthFrame({super.key, required this.title, required this.child});
  final String title;
  final Widget child;
  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(
      actions: [
        TextButton(
          onPressed: () => context.read<LocaleCubit>().toggle(),
          child: Text(AppLocalizations.of(context).languageToggle),
        ),
      ],
    ),
    body: Center(
      child: SingleChildScrollView(
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 460),
          child: Card(
            margin: const EdgeInsets.all(24),
            child: Padding(
              padding: const EdgeInsets.all(24),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Text(title, style: Theme.of(context).textTheme.headlineSmall),
                  const SizedBox(height: 16),
                  child,
                ],
              ),
            ),
          ),
        ),
      ),
    ),
  );
}

final class AuthError extends StatelessWidget {
  const AuthError({super.key, required this.issue});
  final AuthIssue issue;
  @override
  Widget build(BuildContext context) {
    final s = AppLocalizations.of(context);
    final message = switch (issue) {
      AuthIssue.credentials => s.invalidCredentials,
      AuthIssue.mfa => s.invalidMfa,
      AuthIssue.enrollment => s.enrollmentRequired,
      AuthIssue.network => s.authNetworkError,
      AuthIssue.expired => s.sessionExpired,
      AuthIssue.storage => s.authStorageError,
      AuthIssue.configuration => s.authConfigurationError,
    };
    return Semantics(
      liveRegion: true,
      child: Padding(
        padding: const EdgeInsets.symmetric(vertical: 12),
        child: Text(
          message,
          style: TextStyle(color: Theme.of(context).colorScheme.error),
        ),
      ),
    );
  }
}
