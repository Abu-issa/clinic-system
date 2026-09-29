import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';

import '../data/local_ink_draft.dart';
import '../data/notebook_history_api.dart';

/// Read-only transient state: deliberately has no draft store or queue access.
final class NotebookHistory extends ChangeNotifier {
  NotebookHistory(this.api, this.currentBinding);
  final NotebookHistoryApi? api;
  final DraftKey? Function() currentBinding;
  DraftKey? binding;
  bool visible = false,
      listing = false,
      loading = false,
      failed = false,
      hasMore = false;
  int page = 0, latest = 0;
  List<NotebookRevision> items = const [];
  NotebookRevision? selected;
  HistoricalInk? ink;
  int _epoch = 0, _selection = 0;
  CancelToken? _listCancel, _inkCancel;
  bool _disposed = false;
  bool get valid =>
      !_disposed && visible && binding != null && binding == currentBinding();

  Future<void> open() async {
    if (_disposed || api == null || currentBinding() == null) return;
    closeView();
    binding = currentBinding();
    visible = true;
    await loadMore();
  }

  Future<void> loadMore() async {
    if (!valid || listing || (page > 0 && !hasMore)) return;
    final epoch = _epoch, key = binding!;
    final cancel = _listCancel = CancelToken();
    listing = true;
    failed = false;
    notifyListeners();
    try {
      final batch = await api!.list(
        key.patientId,
        key.pageId,
        page + 1,
        cancel,
      );
      if (!valid || epoch != _epoch) return;
      items = List.unmodifiable(
        {
          for (final r in items) r.number: r,
          for (final r in batch.items) r.number: r,
        }.values.toList()..sort((a, b) => b.number.compareTo(a.number)),
      );
      latest = batch.latest;
      page++;
      hasMore = batch.hasMore;
    } catch (_) {
      if (valid && epoch == _epoch) failed = true;
    } finally {
      if (valid && epoch == _epoch) {
        listing = false;
        notifyListeners();
      }
    }
  }

  Future<void> select(NotebookRevision revision) async {
    if (!valid || !items.contains(revision)) return;
    final epoch = _epoch, selection = ++_selection, key = binding!;
    _inkCancel?.cancel();
    final cancel = _inkCancel = CancelToken();
    selected = revision;
    ink = null;
    loading = true;
    failed = false;
    notifyListeners();
    try {
      final result = await api!.read(
        key.patientId,
        key.pageId,
        revision,
        cancel,
      );
      if (valid && epoch == _epoch && selection == _selection) ink = result;
    } catch (_) {
      if (valid && epoch == _epoch && selection == _selection) failed = true;
    } finally {
      if (valid && epoch == _epoch && selection == _selection) {
        loading = false;
        notifyListeners();
      }
    }
  }

  void closeView() {
    _epoch++;
    _selection++;
    _listCancel?.cancel();
    _inkCancel?.cancel();
    visible = false;
    listing = false;
    loading = false;
    failed = false;
    binding = null;
    items = const [];
    selected = null;
    ink = null;
    page = 0;
    latest = 0;
    hasMore = false;
    if (!_disposed) notifyListeners();
  }

  @override
  void dispose() {
    _disposed = true;
    closeView();
    super.dispose();
  }
}
