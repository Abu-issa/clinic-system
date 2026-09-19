import 'package:doctor_tablet/l10n/app_localizations.dart';
import 'package:flutter/material.dart';

/// Shown while the app initializes. The session cubit advances this screen
/// to the login placeholder; this screen never renders protected content.
final class StartupScreen extends StatelessWidget {
  const StartupScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final strings = AppLocalizations.of(context);
    return Scaffold(
      body: Center(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            const Icon(Icons.health_and_safety_outlined, size: 72),
            const SizedBox(height: 16),
            Text(
              strings.appTitle,
              style: Theme.of(context).textTheme.headlineSmall,
            ),
            const SizedBox(height: 4),
            Text(
              strings.startupTagline,
              style: Theme.of(context).textTheme.bodyMedium,
            ),
            const SizedBox(height: 24),
            const CircularProgressIndicator(),
            const SizedBox(height: 16),
            Text(strings.startupInitializing),
          ],
        ),
      ),
    );
  }
}
