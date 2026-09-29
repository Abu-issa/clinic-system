import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:intl/intl.dart' show DateFormat;

import '../../../l10n/app_localizations.dart';
import '../data/notebook_api.dart';
import '../state/notebook_cubit.dart';
import 'ink_page.dart';
import 'conflict_panel.dart';
import 'history_panel.dart';

final class NotebookSection extends StatefulWidget {
  const NotebookSection({super.key, required this.patientId});
  final String patientId;
  @override
  State<NotebookSection> createState() => _NotebookSectionState();
}

final class _NotebookSectionState extends State<NotebookSection> {
  final _title = TextEditingController();
  @override
  void didUpdateWidget(covariant NotebookSection oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.patientId != widget.patientId) _title.clear();
  }

  @override
  void dispose() {
    _title.dispose();
    super.dispose();
  }

  Widget _metadata(NotebookPage page, AppLocalizations s) {
    final format = DateFormat.yMd(Localizations.localeOf(context).languageCode)
        .add_Hm();
    return Text(
      '${s.notebookCreated}: ${format.format(page.created.toLocal())}\n'
      '${s.notebookUpdated}: ${format.format(page.updated.toLocal())}\n'
      '${s.notebookRevision}: ${page.revision} · ${page.finalized ? s.notebookFinalized : s.notebookDraft}',
    );
  }

  @override
  Widget build(
    BuildContext context,
  ) => BlocBuilder<NotebookCubit, NotebookState>(
    builder: (context, state) {
      final cubit = context.read<NotebookCubit>();
      if (!cubit.canRead || state.patientId != widget.patientId) {
        return const SizedBox.shrink();
      }
      final s = AppLocalizations.of(context);
      final issue = switch (state.issue) {
        NotebookIssue.changed => s.notebookChanged,
        NotebookIssue.conflict => s.notebookConflict,
        NotebookIssue.forbidden => s.notebookForbidden,
        NotebookIssue.failed => s.notebookFailed,
        NotebookIssue.documentRejected => s.inkDocumentRejected,
        NotebookIssue.title => s.notebookTitleRequired,
        NotebookIssue.resolutionFailed => s.conflictResolutionFailed,
        NotebookIssue.resolved => s.conflictResolved,
        null => null,
      };
      final selected = state.selected;
      return ListenableBuilder(
        listenable: cubit.history,
        builder: (context, _) => Card(
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Text(
                  s.notebookTitle,
                  style: Theme.of(context).textTheme.headlineSmall,
                ),
                Text(s.notebookFoundation),
                TextButton(
                  onPressed: state.busy ? null : cubit.refresh,
                  child: Text(s.notebookRefresh),
                ),
                if (state.busy) const LinearProgressIndicator(),
                if (issue != null)
                  Semantics(liveRegion: true, child: Text(issue)),
                if (!state.busy && state.items.isEmpty && issue == null)
                  Text(s.notebookEmpty),
                if (cubit.canWrite) ...[
                  TextField(
                    key: const Key('notebook-title'),
                    controller: _title,
                    maxLength: 200,
                    enabled: cubit.canCreate,
                    autocorrect: false,
                    enableSuggestions: false,
                    decoration: InputDecoration(labelText: s.notebookPageTitle),
                  ),
                  FilledButton(
                    key: const Key('notebook-create'),
                    onPressed: !cubit.canCreate
                        ? null
                        : () async {
                            await cubit.create(_title.text);
                            if (mounted && cubit.state.issue == null) {
                              _title.clear();
                            }
                          },
                    child: Text(s.notebookCreate),
                  ),
                ],
                ExpansionTile(
                  key: ValueKey(
                    'pages/${cubit.session.draftOwner}/${widget.patientId}',
                  ),
                  title: Text(s.notebookPages),
                  initiallyExpanded: true,
                  children: [
                    SizedBox(
                      height: 260,
                      child: ListView.builder(
                        key: const Key('notebook-page-list'),
                        itemCount: state.items.length,
                        itemBuilder: (context, index) {
                          final page = state.items[index];
                          final label = switch (state.localStates[page.id]) {
                            'localChanges' => s.inkLocalChanges,
                            'localSaved' => s.inkLocalSaved,
                            'queued' => s.inkQueued,
                            'syncing' => s.inkSyncing,
                            'serverSynced' => s.inkServerSynced,
                            'offline' => s.inkOffline,
                            'conflict' => s.inkConflict,
                            'syncFailed' => s.inkSyncFailed,
                            _ => null,
                          };
                          return ListTile(
                            key: Key('notebook-page-${page.id}'),
                            title: Text(page.title),
                            subtitle: Column(
                              crossAxisAlignment: CrossAxisAlignment.start,
                              children: [
                                _metadata(page, s),
                                if (label != null)
                                  Text(
                                    label,
                                    key: Key('notebook-status-${page.id}'),
                                  ),
                              ],
                            ),
                            selected: selected?.id == page.id,
                            onTap: state.busy
                                ? null
                                : () => cubit.open(page.id),
                          );
                        },
                      ),
                    ),
                    if (state.hasMore)
                      TextButton(
                        key: const Key('notebook-load-more'),
                        onPressed: state.busy ? null : cubit.loadMore,
                        child: Text(s.notebookLoadMore),
                      ),
                  ],
                ),
                if (selected != null &&
                    selected.patientId == widget.patientId) ...[
                  const Divider(),
                  Wrap(
                    alignment: WrapAlignment.spaceBetween,
                    crossAxisAlignment: WrapCrossAlignment.center,
                    children: [
                      TextButton(
                        key: const Key('notebook-previous'),
                        onPressed: !state.busy && cubit.hasPrevious
                            ? cubit.previous
                            : null,
                        child: Text(s.notebookPrevious),
                      ),
                      if (cubit.selectedIndex >= 0)
                        Text(
                          s.notebookPosition(
                            '${cubit.selectedIndex + 1}',
                            '${state.items.length}${state.hasMore ? '+' : ''}',
                          ),
                          key: const Key('notebook-position'),
                        ),
                      TextButton(
                        key: const Key('notebook-next'),
                        onPressed: !state.busy && cubit.hasNext
                            ? cubit.next
                            : null,
                        child: Text(s.notebookNext),
                      ),
                    ],
                  ),
                  Text(
                    selected.title,
                    style: Theme.of(context).textTheme.titleLarge,
                  ),
                  _metadata(selected, s),
                  if (cubit.activeDraft case final draft?)
                    ListenableBuilder(
                      listenable: Listenable.merge([draft, draft.ink.active]),
                      builder: (context, _) => Column(
                        children: [
                          if (cubit.history.visible)
                            Text(
                              inkSaveLabel(
                                s,
                                draft,
                                draft.ink.active.points.isNotEmpty,
                              ),
                              key: const Key('history-current-status'),
                            ),
                          if (!cubit.history.visible)
                            OutlinedButton(
                              key: const Key('notebook-history'),
                              onPressed: state.busy || draft.ink.hasContact
                                  ? null
                                  : cubit.showHistory,
                              child: Text(s.historyTitle),
                            ),
                        ],
                      ),
                    ),
                  if (cubit.history.visible)
                    HistoryPanel(history: cubit.history),
                  if (!cubit.history.visible) ...[
                    if (cubit.canWrite &&
                        cubit.activeDraft?.syncState == 'conflict')
                      ConflictPanel(
                        key: ValueKey(
                          '${cubit.session.draftOwner}/${selected.patientId}/${selected.id}',
                        ),
                        cubit: cubit,
                      ),
                    InkPage(
                      key: ValueKey(
                        'ink/${cubit.session.draftOwner}/${selected.patientId}/${selected.id}',
                      ),
                      drafts: cubit.session.drafts,
                      selectedDraft: cubit.activeDraft,
                      owner: cubit.session.draftOwner,
                      serverRevision: selected.revision,
                      serverRowVersion: selected.rowVersion,
                      patientId: widget.patientId,
                      pageId: selected.id,
                      enabled: cubit.canDraw,
                      isCurrent: () =>
                          cubit.ownsPage(widget.patientId, selected.id),
                    ),
                    if (cubit.activeDraft?.syncState != 'conflict') ...[
                      Text(s.notebookRowVersion),
                      SelectableText(
                        selected.rowVersion,
                        textDirection: TextDirection.ltr,
                      ),
                    ],
                    if (cubit.canWrite) ...[
                      if (!selected.finalized) ...[
                        FilledButton(
                          key: const Key('notebook-revise'),
                          onPressed: cubit.canRevise
                              ? () => cubit.submit()
                              : null,
                          child: Text(s.notebookSubmit),
                        ),
                        if (cubit.activeDraft case final draft?)
                          ListenableBuilder(
                            listenable: Listenable.merge([
                              draft,
                              draft.ink.active,
                            ]),
                            builder: (context, _) => OutlinedButton(
                              key: const Key('notebook-finalize'),
                              onPressed: cubit.canFinalize
                                  ? cubit.finalize
                                  : null,
                              child: Text(s.notebookFinalize),
                            ),
                          ),
                      ] else
                        FilledButton(
                          key: const Key('notebook-amend'),
                          onPressed: cubit.canAmend
                              ? () => cubit.editingAmendment
                                    ? cubit.submit(amendment: true)
                                    : cubit.beginAmendment()
                              : null,
                          child: Text(
                            cubit.editingAmendment
                                ? s.notebookAmend
                                : s.inkBeginAmendment,
                          ),
                        ),
                      if (cubit.activeDraft case final draft?)
                        ListenableBuilder(
                          listenable: draft,
                          builder: (context, _) =>
                              draft.queuedCount > 0 &&
                                  !state.blocked &&
                                  draft.syncState != 'syncFailed' &&
                                  draft.syncState != 'conflict'
                              ? OutlinedButton(
                                  onPressed: state.busy || draft.syncing
                                      ? null
                                      : cubit.retry,
                                  child: Text(s.notebookRetry),
                                )
                              : const SizedBox.shrink(),
                        ),
                    ],
                  ],
                ],
              ],
            ),
          ),
        ),
      );
    },
  );
}
