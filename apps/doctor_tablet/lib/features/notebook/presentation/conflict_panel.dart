import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import '../../../l10n/app_localizations.dart';
import '../state/notebook_cubit.dart';

/// Inline resolution keeps the app's patient header visible throughout.
class ConflictPanel extends StatefulWidget {
  const ConflictPanel({super.key, required this.cubit});
  final NotebookCubit cubit;
  @override
  State<ConflictPanel> createState() => _ConflictPanelState();
}

class _ConflictPanelState extends State<ConflictPanel> {
  bool later = false, confirmDiscard = false;
  @override
  Widget build(BuildContext context) {
    final cubit = widget.cubit, draft = widget.cubit.activeDraft;
    final selected = cubit.state.selected;
    if (draft == null || selected == null || draft.syncState != 'conflict') {
      return const SizedBox.shrink();
    }
    final s = AppLocalizations.of(context);
    if (later) {
      return TextButton(
        key: const Key('conflict-reopen'),
        onPressed: () => setState(() => later = false),
        child: Text(s.conflictDetected),
      );
    }
    final server = cubit.conflictServer;
    final known =
        server != null &&
            server.patientId == selected.patientId &&
            server.id == selected.id
        ? server
        : selected;
    final format = DateFormat.yMd(Localizations.localeOf(context).languageCode)
        .add_Hm();
    return Card(
      key: const Key('conflict-panel'),
      child: Padding(
        padding: const EdgeInsets.all(12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(
              s.conflictDetected,
              style: Theme.of(context).textTheme.titleMedium,
            ),
            Text(selected.title),
            Text(
              '${s.conflictLocalSaved}: ${draft.lastSavedAt == null ? s.conflictUnknown : format.format(draft.lastSavedAt!.toLocal())}',
            ),
            Text('${s.conflictQueuedCount}: ${draft.queuedCount}'),
            Text('${s.conflictServerRevision}: ${known.revision}'),
            Text(
              '${s.conflictServerUpdated}: ${format.format(known.updated.toLocal())}',
            ),
            Text(s.conflictExplanation),
            if (confirmDiscard)
              Text(s.conflictConfirmDiscard, key: const Key('discard-warning')),
            Wrap(
              spacing: 8,
              runSpacing: 8,
              children: [
                TextButton(
                  key: const Key('conflict-later'),
                  onPressed: cubit.state.busy
                      ? null
                      : () => setState(() {
                          later = true;
                          confirmDiscard = false;
                        }),
                  child: Text(s.conflictLater),
                ),
                OutlinedButton(
                  key: const Key('conflict-discard'),
                  onPressed: cubit.state.busy
                      ? null
                      : () async {
                          if (!confirmDiscard) {
                            setState(() => confirmDiscard = true);
                            return;
                          }
                          await cubit.resolveConflict(
                            ConflictChoice.discard,
                            confirmed: true,
                          );
                          if (mounted) setState(() => confirmDiscard = false);
                        },
                  child: Text(
                    confirmDiscard
                        ? s.conflictConfirmAction
                        : s.conflictDiscard,
                  ),
                ),
                FilledButton(
                  key: const Key('conflict-save'),
                  onPressed: cubit.state.busy
                      ? null
                      : () => cubit.resolveConflict(ConflictChoice.saveAsNew),
                  child: Text(s.conflictSaveNew),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}
