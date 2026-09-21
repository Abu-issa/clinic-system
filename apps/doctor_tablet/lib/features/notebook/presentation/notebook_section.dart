import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:intl/intl.dart' show DateFormat;

import '../../../l10n/app_localizations.dart';
import '../data/notebook_api.dart';
import '../state/notebook_cubit.dart';
import 'ink_page.dart';

final class NotebookSection extends StatefulWidget {
  const NotebookSection({super.key, required this.patientId});
  final String patientId;
  @override
  State<NotebookSection> createState() => _NotebookSectionState();
}

final class _NotebookSectionState extends State<NotebookSection> {
  final _title = TextEditingController();
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
        NotebookIssue.title => s.notebookTitleRequired,
        null => null,
      };
      final selected = state.selected;
      return Card(
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
                  enabled: cubit.canMutate,
                  autocorrect: false,
                  enableSuggestions: false,
                  decoration: InputDecoration(labelText: s.notebookPageTitle),
                ),
                FilledButton(
                  key: const Key('notebook-create'),
                  onPressed: !cubit.canMutate
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
              for (final page in state.items)
                ListTile(
                  key: Key('notebook-page-${page.id}'),
                  title: Text(page.title),
                  subtitle: _metadata(page, s),
                  selected: selected?.id == page.id,
                  onTap: state.busy || state.pending != null
                      ? null
                      : () => cubit.open(page.id),
                ),
              if (state.hasMore)
                TextButton(
                  onPressed: state.busy || state.pending != null
                      ? null
                      : cubit.loadMore,
                  child: Text(s.notebookLoadMore),
                ),
              if (selected != null &&
                  selected.patientId == widget.patientId) ...[
                const Divider(),
                Text(
                  selected.title,
                  style: Theme.of(context).textTheme.titleLarge,
                ),
                _metadata(selected, s),
                InkPage(
                  patientId: widget.patientId,
                  pageId: selected.id,
                  enabled: cubit.canRevise,
                  isCurrent: () =>
                      cubit.ownsPage(widget.patientId, selected.id),
                ),
                Text(s.notebookRowVersion),
                SelectableText(
                  selected.rowVersion,
                  textDirection: TextDirection.ltr,
                ),
                if (cubit.canWrite) ...[
                  if (!selected.finalized) ...[
                    FilledButton(
                      key: const Key('notebook-revise'),
                      onPressed: cubit.canRevise ? () => cubit.submit() : null,
                      child: Text(s.notebookSubmit),
                    ),
                    OutlinedButton(
                      key: const Key('notebook-finalize'),
                      onPressed: cubit.canRevise ? cubit.finalize : null,
                      child: Text(s.notebookFinalize),
                    ),
                  ] else
                    FilledButton(
                      key: const Key('notebook-amend'),
                      onPressed: cubit.canAmend
                          ? () => cubit.submit(amendment: true)
                          : null,
                      child: Text(s.notebookAmend),
                    ),
                  if (state.pending != null && !state.blocked) ...[
                    Text(s.notebookRetryHint),
                    OutlinedButton(
                      onPressed: state.busy ? null : cubit.retry,
                      child: Text(s.notebookRetry),
                    ),
                  ],
                ],
              ],
            ],
          ),
        ),
      );
    },
  );
}
