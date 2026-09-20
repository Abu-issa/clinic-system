import 'package:doctor_tablet/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../../app/session/session_cubit.dart';
import 'auth_widgets.dart';

final class LoginScreen extends StatefulWidget {
  const LoginScreen({super.key});
  @override
  State<LoginScreen> createState() => _LoginScreenState();
}

final class _LoginScreenState extends State<LoginScreen> {
  final _login = TextEditingController();
  final _password = TextEditingController();
  final _form = GlobalKey<FormState>();
  @override
  void dispose() {
    _login.dispose();
    _password.dispose();
    super.dispose();
  }

  void _submit() {
    if (!_form.currentState!.validate()) return;
    final password = _password.text;
    _password.clear();
    context.read<SessionCubit>().login(_login.text, password);
  }

  @override
  Widget build(BuildContext context) {
    final strings = AppLocalizations.of(context);
    final state = context.watch<SessionCubit>().state;
    final busy = state is SessionPasswordLoading;
    final issue = switch (state) {
      SessionUnauthenticated(:final issue) => issue,
      SessionExpired(:final issue) => issue,
      _ => null,
    };
    return AuthFrame(
      title: strings.loginTitle,
      child: Form(
        key: _form,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          mainAxisSize: MainAxisSize.min,
          children: [
            Text(strings.loginNote),
            const SizedBox(height: 16),
            TextFormField(
              key: const Key('login'),
              controller: _login,
              enabled: !busy,
              decoration: InputDecoration(labelText: strings.loginIdentifier),
              autocorrect: false,
              enableSuggestions: false,
              maxLength: 256,
              validator: (v) =>
                  v == null || v.trim().isEmpty ? strings.requiredField : null,
            ),
            TextFormField(
              key: const Key('password'),
              controller: _password,
              enabled: !busy,
              decoration: InputDecoration(labelText: strings.loginPassword),
              obscureText: true,
              autocorrect: false,
              enableSuggestions: false,
              maxLength: 1024,
              validator: (v) =>
                  v == null || v.isEmpty ? strings.requiredField : null,
              onFieldSubmitted: (_) {
                if (!busy) _submit();
              },
            ),
            if (issue != null) AuthError(issue: issue),
            const SizedBox(height: 16),
            FilledButton(
              key: const Key('sign-in'),
              onPressed: busy ? null : _submit,
              child: Text(busy ? strings.signingIn : strings.signIn),
            ),
          ],
        ),
      ),
    );
  }
}
