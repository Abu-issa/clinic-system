import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import '../../../l10n/app_localizations.dart';
import '../state/notebook_history.dart';
import 'ink_page.dart';

final class HistoryPanel extends StatelessWidget {
  const HistoryPanel({super.key, required this.history});
  final NotebookHistory history;
  @override
  Widget build(BuildContext context) {
    final s = AppLocalizations.of(context);
    final h = history;
    if (!h.valid) return const SizedBox.shrink();
    final format = DateFormat.yMd(Localizations.localeOf(context).languageCode)
        .add_Hm();
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text(s.historyReadOnly, key: const Key('history-read-only')),
        OutlinedButton(
          key: const Key('history-back'),
          onPressed: h.closeView,
          child: Text(s.historyBack),
        ),
        if (h.listing || h.loading) const LinearProgressIndicator(),
        if (h.failed) Text(s.historyFailed, key: const Key('history-error')),
        if (h.failed && h.page == 0)
          TextButton(onPressed: h.loadMore, child: Text(s.notebookRetry)),
        SizedBox(
          height: 240,
          child: ListView.builder(
            key: const Key('history-list'),
            itemCount: h.items.length,
            itemBuilder: (context, index) {
              final r = h.items[index];
              final kind = switch (r.kind) {
                'Created' => s.historyCreated,
                'Amendment' => s.historyAmendment,
                _ => s.historyRevision,
              };
              return ListTile(
                key: Key('history-revision-${r.number}'),
                selected: h.selected?.number == r.number,
                title: Text(
                  '${s.notebookRevision} ${r.number} · $kind${r.number == h.latest ? ' · ${s.historyLatest}' : ''}',
                ),
                subtitle: Text(
                  '${s.historyAuthor}: ${r.author}\n${format.format(r.created.toLocal())}',
                ),
                onTap: () => h.select(r),
              );
            },
          ),
        ),
        if (h.hasMore)
          TextButton(
            key: const Key('history-more'),
            onPressed: h.listing ? null : h.loadMore,
            child: Text(s.notebookLoadMore),
          ),
        if (h.selected case final revision?)
          Text('${s.historySelected}: ${revision.number}'),
        if (h.ink case final ink?) ...[
          if (ink.legacy)
            Text(s.historyLegacy, key: const Key('history-legacy')),
          if (h.selected?.hasPayload == false) Text(s.historyNoInk),
          // No InkController, DraftHandle, pointer Listener, or mutation controls.
          RepaintBoundary(
            child: AspectRatio(
              aspectRatio: 210 / 297,
              child: ClipRect(
                child: ColoredBox(
                  color: Colors.white,
                  child: CommittedInkView(
                    strokes: ink.document.strokes,
                    paintKey: const Key('history-canvas'),
                  ),
                ),
              ),
            ),
          ),
        ],
      ],
    );
  }
}
