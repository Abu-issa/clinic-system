import 'dart:async';
import 'package:flutter/foundation.dart';
import '../data/encrypted_draft_store.dart';
import '../data/local_ink_draft.dart';
import '../ink/ink_controller.dart';

/// App/session-owned, not page-owned: failed saves survive page disposal.
final class LocalDrafts {
  LocalDrafts(this.store);
  final DraftStore store;
  final _entries = <DraftKey, DraftHandle>{};
  bool _logoutPending = false;

  DraftHandle open(DraftKey key, {int? revision, String? rowVersion}) {
    final handle = _entries.putIfAbsent(key, () => DraftHandle(store, key, revision, rowVersion));
    handle.attached = true;
    handle.locked = _logoutPending;
    return handle;
  }
  void leave(DraftHandle handle) {
    handle.ink.finishActive();
    handle.attached = false;
    unawaited(handle.flush().then((success) {
      if (success && !handle.attached && !handle.dirty && identical(_entries[handle.key], handle)) {
        _entries.remove(handle.key);
        handle.dispose();
      }
    }));
  }
  Future<bool> flushAll({bool forLogout = false}) async {
    if (forLogout) {
      if (_logoutPending) return false;
      _logoutPending = true;
    }
    final handles = _entries.values.toList();
    try {
      for (final h in handles) {
        if (forLogout) h.setLocked(true);
        h.ink.finishActive();
      }
      final results = await Future.wait(handles.map((h) => h.flush()));
      return results.every((ok) => ok);
    } finally {
      if (forLogout) {
        _logoutPending = false;
        for (final h in handles) { if (!h.disposed) h.setLocked(false); }
      }
    }
  }
  Future<void> close() async {
    if (!await flushAll()) return; // Keep failed, unsaved drafts in memory.
    for (final h in _entries.values) { h.dispose(); }
    _entries.clear();
    await store.close();
  }
}

final class DraftHandle extends ChangeNotifier {
  DraftHandle(this.store, this.key, this.revision, this.rowVersion)
    : ink = InkController(key.patientId, key.pageId) {
    ink.addListener(_edited);
    ready = _restore();
  }
  final DraftStore store;
  final DraftKey key;
  final InkController ink;
  int? revision;
  String? rowVersion;
  late final Future<void> ready;
  bool loading = true, failed = false, dirty = false, saved = false,
    attached = true, locked = false, disposed = false;
  bool get canEdit => !loading && !failedRestore && !locked;
  bool failedRestore = false;
  int _generation = 0;
  Timer? _debounce, _checkpoint;
  Future<bool>? _flight;

  Future<void> _restore() async {
    try {
      final draft = await store.read(key);
      if (draft != null) {
        // Check again even with an alternate/injected repository implementation.
        if (draft.key != key || !draft.document.matches(key.patientId, key.pageId)) {
          throw const FormatException('Draft binding mismatch');
        }
        ink.restore(draft.document);
        revision = draft.serverRevision;
        rowVersion = draft.serverRowVersion;
        saved = true;
      }
    } catch (_) {
      failedRestore = true; failed = true;
    } finally {
      loading = false;
      notifyListeners();
    }
  }
  Future<void> retryRestore() async {
    if (loading || !failedRestore) return;
    loading = true; failedRestore = false; failed = false;
    notifyListeners();
    await _restore();
  }
  void _edited() {
    if (loading || disposed) return;
    _generation++;
    dirty = true; saved = false;
    _debounce?.cancel();
    _debounce = Timer(const Duration(milliseconds: 1500), () => unawaited(flush()));
    // Does not slide with each edit: sustained drawing still checkpoints.
    _checkpoint ??= Timer.periodic(const Duration(seconds: 30), (_) {
      if (dirty) unawaited(flush());
    });
    notifyListeners();
  }
  void setLocked(bool value) { locked = value; notifyListeners(); }

  Future<bool> flush() async {
    await ready;
    if (failedRestore) return false;
    _debounce?.cancel();
    final flight = _flight;
    if (flight != null) {
      if (!await flight) return false;
      return dirty ? flush() : true;
    }
    if (!dirty) return true;
    final generation = _generation;
    final draft = LocalInkDraft(key: key, document: ink.document,
      updatedAt: DateTime.now().toUtc(), serverRevision: revision, serverRowVersion: rowVersion);
    final task = _write(draft, generation);
    _flight = task;
    try { return await task; } finally { _flight = null; }
  }
  Future<bool> _write(LocalInkDraft draft, int generation) async {
    try {
      await store.write(draft);
      if (generation == _generation) {
        dirty = false; saved = true;
        _checkpoint?.cancel(); _checkpoint = null;
      }
      failed = false;
      notifyListeners();
      return true;
    } catch (_) {
      failed = true; saved = false;
      notifyListeners();
      return false;
    }
  }
  @override
  void dispose() {
    disposed = true;
    _debounce?.cancel(); _checkpoint?.cancel();
    ink.removeListener(_edited); ink.dispose(); super.dispose();
  }
}
