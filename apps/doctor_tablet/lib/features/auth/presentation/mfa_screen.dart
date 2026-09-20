import 'package:doctor_tablet/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../../app/session/session_cubit.dart';
import 'auth_widgets.dart';

final class MfaScreen extends StatefulWidget {
  const MfaScreen({super.key});
  @override
  State<MfaScreen> createState() => _MfaScreenState();
}

final class _MfaScreenState extends State<MfaScreen> {
  final _code = TextEditingController();
  final _form = GlobalKey<FormState>();
  @override
  void dispose() {
    _code.dispose();
    super.dispose();
  }

  void _submit() {
    if (!_form.currentState!.validate()) return;
    final code = _code.text;
    _code.clear();
    context.read<SessionCubit>().verify(code);
  }

  @override
  Widget build(BuildContext context) {
    final s = AppLocalizations.of(context);
    final state = context.watch<SessionCubit>().state;
    if (state is! SessionMfaRequired) return const SizedBox.shrink();
    return PopScope(
      canPop: false,
      onPopInvokedWithResult: (didPop, _) {
        if (!didPop && !state.busy) context.read<SessionCubit>().cancelMfa();
      },
      child: AuthFrame(
        title: s.mfaTitle,
        child: Form(
          key: _form,
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Text(s.mfaNote),
              const SizedBox(height: 16),
              TextFormField(
                key: const Key('mfa-code'),
                controller: _code,
                enabled: !state.busy,
                obscureText: true,
                enableSuggestions: false,
                autocorrect: false,
                keyboardType: TextInputType.number,
                textDirection: TextDirection.ltr,
                inputFormatters: [FilteringTextInputFormatter.digitsOnly],
                maxLength: 6,
                decoration: InputDecoration(labelText: s.mfaCode),
                validator: (v) => v?.length != 6 ? s.mfaCodeRequired : null,
                onFieldSubmitted: (_) {
                  if (!state.busy) _submit();
                },
              ),
              if (state.issue != null) AuthError(issue: state.issue!),
              FilledButton(
                key: const Key('verify-mfa'),
                onPressed: state.busy ? null : _submit,
                child: Text(state.busy ? s.verifyingMfa : s.verifyMfa),
              ),
              TextButton(
                onPressed: state.busy
                    ? null
                    : () => context.read<SessionCubit>().cancelMfa(),
                child: Text(s.backToLogin),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
