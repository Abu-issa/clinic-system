import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:doctor_tablet/l10n/app_localizations.dart';

import '../../../app/session/session_cubit.dart';
import '../data/patient_search.dart';
import '../state/patient_context_cubit.dart';

String allergyLabel(AppLocalizations s, PatientAllergyStatus status) =>
    switch (status) {
      PatientAllergyStatus.unknown => s.patientAllergyUnknown,
      PatientAllergyStatus.noKnownAllergies => s.patientAllergyNone,
      PatientAllergyStatus.hasKnownAllergies => s.patientAllergyKnown,
    };

final class PatientHeader extends StatelessWidget {
  const PatientHeader({super.key, required this.patient});
  final PatientContext patient;
  @override
  Widget build(BuildContext context) {
    final s = AppLocalizations.of(context);
    final dob = patient.dateOfBirth;
    return Material(
      color: Theme.of(context).colorScheme.surfaceContainerHighest,
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          mainAxisSize: MainAxisSize.min,
          children: [
            Text(
              s.patientActive,
              style: Theme.of(context).textTheme.labelLarge,
            ),
            Text(
              patient.fullName,
              key: const Key('active-patient-name'),
              style: Theme.of(context).textTheme.titleLarge,
            ),
            Wrap(
              spacing: 24,
              runSpacing: 8,
              children: [
                Text('${s.patientMrn}: ${patient.mrn ?? s.patientNotRecorded}'),
                Text(
                  '${s.patientDob}: ${dob == null ? s.patientNotRecorded : MaterialLocalizations.of(context).formatMediumDate(dob)}',
                ),
                Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Icon(
                      patient.allergyStatus ==
                              PatientAllergyStatus.hasKnownAllergies
                          ? Icons.warning_amber
                          : Icons.info_outline,
                    ),
                    const SizedBox(width: 8),
                    Flexible(
                      child: Text(
                        allergyLabel(s, patient.allergyStatus),
                        key: const Key('active-patient-allergy'),
                      ),
                    ),
                  ],
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}

final class PatientWorkspace extends StatefulWidget {
  const PatientWorkspace({super.key});
  @override
  State<PatientWorkspace> createState() => _PatientWorkspaceState();
}

final class _PatientWorkspaceState extends State<PatientWorkspace> {
  final _term = TextEditingController();
  @override
  void dispose() {
    _term.dispose();
    super.dispose();
  }

  Future<void> _select(PatientContext patient) async {
    final cubit = context.read<PatientContextCubit>();
    final active = cubit.state.active;
    if (active != null && active.patientId != patient.patientId) {
      final s = AppLocalizations.of(context);
      final confirmed = await showDialog<bool>(
        context: context,
        builder: (context) => BlocListener<SessionCubit, SessionState>(
          listenWhen: (previous, next) =>
              (previous is SessionAuthenticated ||
                  previous is SessionRefreshing) &&
              next is! SessionAuthenticated &&
              next is! SessionRefreshing,
          listener: (context, _) => Navigator.pop(context, false),
          child: AlertDialog(
            title: Text(s.patientSwitchTitle),
            content: Text(
              s.patientSwitchMessage(active.fullName, patient.fullName),
            ),
            actions: [
              TextButton(
                onPressed: () => Navigator.pop(context, false),
                child: Text(s.patientSwitchCancel),
              ),
              FilledButton(
                key: const Key('confirm-patient-switch'),
                onPressed: () => Navigator.pop(context, true),
                child: Text(s.patientSwitchConfirm),
              ),
            ],
          ),
        ),
      );
      if (!mounted || confirmed != true) return;
    }
    cubit.select(patient);
  }

  @override
  Widget build(BuildContext context) {
    final s = AppLocalizations.of(context);
    final refreshing = context.watch<SessionCubit>().state is SessionRefreshing;
    return BlocBuilder<PatientContextCubit, PatientContextState>(
      builder: (context, state) {
        final cubit = context.read<PatientContextCubit>();
        final error = switch (state.issue) {
          PatientSearchIssue.invalidTerm => s.patientInvalidTerm,
          PatientSearchIssue.forbidden => s.patientForbidden,
          PatientSearchIssue.unauthorized => s.sessionExpired,
          PatientSearchIssue.network => s.patientNetworkError,
          null => null,
        };
        return Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            if (state.active != null)
              PatientHeader(
                key: const Key('patient-header'),
                patient: state.active!,
              ),
            if (refreshing) const LinearProgressIndicator(),
            Expanded(
              child: ListView(
                padding: const EdgeInsets.all(16),
                children: [
                  Text(
                    s.patientSearchTitle,
                    style: Theme.of(context).textTheme.titleLarge,
                  ),
                  const SizedBox(height: 12),
                  TextField(
                    key: const Key('patient-search-term'),
                    controller: _term,
                    maxLength: 100,
                    autocorrect: false,
                    enableSuggestions: false,
                    enabled: !refreshing,
                    decoration: InputDecoration(labelText: s.patientSearchTerm),
                    textInputAction: TextInputAction.search,
                    onSubmitted: refreshing
                        ? null
                        : (_) => cubit.search(_term.text),
                  ),
                  FilledButton(
                    key: const Key('patient-search-submit'),
                    onPressed: refreshing
                        ? null
                        : () => cubit.search(_term.text),
                    child: Text(s.patientSearchAction),
                  ),
                  if (error != null)
                    Padding(
                      padding: const EdgeInsets.symmetric(vertical: 12),
                      child: Semantics(
                        liveRegion: true,
                        child: Text(
                          error,
                          style: TextStyle(
                            color: Theme.of(context).colorScheme.error,
                          ),
                        ),
                      ),
                    ),
                  if (state.busy)
                    const Padding(
                      padding: EdgeInsets.all(12),
                      child: LinearProgressIndicator(),
                    ),
                  if (!state.searched && !state.busy && error == null)
                    Text(s.patientSearchHint),
                  if (state.searched &&
                      state.items.isEmpty &&
                      !state.busy &&
                      error == null)
                    Text(s.patientSearchEmpty),
                  for (final patient in state.items)
                    Card(
                      child: ListTile(
                        title: Text(patient.fullName),
                        subtitle: Text(
                          '${s.patientMrn}: ${patient.mrn ?? s.patientNotRecorded}',
                        ),
                        trailing: TextButton(
                          key: Key('select-${patient.patientId}'),
                          onPressed: refreshing ? null : () => _select(patient),
                          child: Text(
                            state.active?.patientId == patient.patientId
                                ? s.patientSelected
                                : state.active == null
                                ? s.patientSelect
                                : s.patientSwitchConfirm,
                          ),
                        ),
                      ),
                    ),
                  if (state.hasMore)
                    TextButton(
                      key: const Key('patient-load-more'),
                      onPressed: state.busy || refreshing
                          ? null
                          : cubit.loadMore,
                      child: Text(s.patientLoadMore),
                    ),
                ],
              ),
            ),
          ],
        );
      },
    );
  }
}
