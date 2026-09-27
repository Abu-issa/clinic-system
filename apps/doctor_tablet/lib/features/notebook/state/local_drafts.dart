import 'dart:async';

import 'package:flutter/foundation.dart';

import '../data/encrypted_draft_store.dart';
import '../data/local_ink_draft.dart';
import '../ink/ink_controller.dart';
import '../data/notebook_api.dart';
import '../data/server_ink_codec.dart';

/// App/session-owned, not page-owned: failed saves survive page disposal.
final class LocalDrafts extends ChangeNotifier {
  LocalDrafts(this.store);
  final DraftStore store;
  final _entries = <DraftKey, DraftHandle>{};
  final statuses = <DraftKey, String>{};
  DraftHandle _new(DraftKey key, int? revision, String? rowVersion) {
    final handle = DraftHandle(store, key, revision, rowVersion);
    void changed() {
      final status = handle.syncing
          ? 'syncing'
          : handle.syncState == 'conflict'
          ? 'conflict'
          : handle.syncState == 'syncFailed'
          ? 'syncFailed'
          : handle.dirty || handle.ink.active.points.isNotEmpty
          ? 'localChanges'
          : handle.syncState == 'offline'
          ? 'offline'
          : handle.queuedCount > 0
          ? 'queued'
          : handle.serverSynced
          ? 'serverSynced'
          : handle.saved
          ? 'localSaved'
          : 'localChanges';
      if (statuses[key] != status) {
        statuses[key] = status;
        notifyListeners();
      }
    }

    handle.addListener(changed);
    handle.ink.active.addListener(changed);
    return handle;
  }

  bool _logoutPending = false;
  DraftHandle? find(DraftKey key) => _entries[key];

  DraftHandle forSync(DraftKey key) =>
      _entries.putIfAbsent(key, () => _new(key, null, null)..attached = false);

  DraftHandle open(DraftKey key, {int? revision, String? rowVersion}) {
    final handle = _entries.putIfAbsent(
      key,
      () => _new(key, revision, rowVersion),
    );
    handle.attached = true;
    handle.locked = _logoutPending;
    return handle;
  }

  void leave(DraftHandle handle) {
    unawaited(release(handle));
  }

  Future<bool> release(DraftHandle handle) async {
    if (handle.disposed) return true;
    handle.ink.finishActive();
    handle.ink.releaseHistory();
    handle.attached = false;
    // A rejected restore has no editable memory state to save. The encrypted
    // row remains untouched; navigating away must not trap other pages.
    final success =
        handle.failedRestore && !handle.dirty || await handle.flush();
    if (success &&
        !handle.attached &&
        !handle.dirty &&
        !handle.syncing &&
        identical(_entries[handle.key], handle)) {
      _entries.remove(handle.key);
      handle.dispose();
    }
    return success;
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
        for (final h in handles) {
          if (!h.disposed) h.setLocked(false);
        }
      }
    }
  }

  Future<void> close() async {
    if (!await flushAll()) return; // Keep failed, unsaved drafts in memory.
    if (_entries.values.any((h) => h.syncing)) return;
    for (final h in _entries.values) {
      h.dispose();
    }
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
  String syncState = 'localOnly';
  NotebookDraft? submission;
  NotebookDraft? resolution;
  DateTime? lastSavedAt;
  bool syncing = false;
  int queuedCount = 0;
  bool get serverSynced =>
      queuedCount == 0 &&
      syncState == 'serverSynced' &&
      saved &&
      !dirty &&
      !syncing &&
      submission == null &&
      ink.active.points.isEmpty;
  late Future<void> ready;
  bool loading = true,
      failed = false,
      dirty = false,
      saved = false,
      attached = true,
      locked = false,
      disposed = false;
  bool get canEdit => !disposed && !loading && !failedRestore && !locked;
  bool hasPersisted = false;
  bool failedRestore = false;
  int _generation = 0;
  Timer? _debounce, _checkpoint;
  Future<bool>? _flight;

  Future<void> _restore() async {
    try {
      final draft = await store.read(key);
      if (draft != null) {
        // Check again even with an alternate/injected repository implementation.
        if (draft.key != key ||
            !draft.document.matches(key.patientId, key.pageId)) {
          throw const FormatException('Draft binding mismatch');
        }
        ink.restore(draft.document);
        revision = draft.serverRevision;
        rowVersion = draft.serverRowVersion;
        syncState = draft.syncState;
        submission = draft.submission;
        resolution = draft.resolution;
        lastSavedAt = draft.updatedAt;
        saved = true;
        hasPersisted = true;
      }
    } catch (_) {
      failedRestore = true;
      failed = true;
    } finally {
      loading = false;
      notifyListeners();
    }
  }

  Future<void> retryRestore() async {
    if (loading || !failedRestore) return;
    loading = true;
    failedRestore = false;
    failed = false;
    notifyListeners();
    ready = _restore();
    await ready;
  }

  void _edited() {
    if (loading || disposed) return;
    if (syncState == 'serverSynced') syncState = 'localOnly';
    _markDirty();
  }

  void _markDirty() {
    _generation++;
    dirty = true;
    saved = false;
    _debounce?.cancel();
    _debounce = Timer(
      const Duration(milliseconds: 1500),
      () => unawaited(flush()),
    );
    // Does not slide with each edit: sustained drawing still checkpoints.
    _checkpoint ??= Timer.periodic(const Duration(seconds: 30), (_) {
      if (dirty) unawaited(flush());
    });
    notifyListeners();
  }

  void setLocked(bool value) {
    locked = value;
    notifyListeners();
  }

  Future<void> recordFinalization(NotebookPage page) async {
    revision = page.revision;
    rowVersion = page.rowVersion;
    _markDirty();
    await flush();
  }

  Future<void> recordConflict() async {
    syncState = 'conflict';
    _markDirty();
    await flush();
  }

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
    final draft = LocalInkDraft(
      key: key,
      document: ink.document,
      updatedAt: DateTime.now().toUtc(),
      serverRevision: revision,
      serverRowVersion: rowVersion,
      syncState: syncState,
      submission: submission,
      resolution: resolution,
    );
    final task = _write(draft, generation);
    _flight = task;
    try {
      return await task;
    } finally {
      _flight = null;
    }
  }

  Future<NotebookDraft?> prepareSubmission(
    NotebookPage page,
    String device, {
    bool amendment = false,
  }) async {
    await ready;
    if (!canEdit ||
        syncState == 'conflict' ||
        syncState == 'syncFailed' ||
        !ink.document.matches(page.patientId, page.id)) {
      return null;
    }
    ink.finishActive();
    if (submission == null) {
      final bytes = await compute(encodeServerInk, ink.document);
      submission = NotebookDraft(
        patientId: key.patientId,
        pageId: key.pageId,
        expectedRowVersion: rowVersion ?? page.rowVersion,
        originDeviceId: device,
        amendment: amendment,
        payload: bytes,
      );
      _markDirty();
    }
    return await flush() ? submission : null;
  }

  Future<bool> queueState(String value, {bool clearSubmission = false}) async {
    syncState = value;
    if (clearSubmission) submission = null;
    _markDirty();
    return flush();
  }

  void setSyncing(bool value) {
    syncing = value;
    notifyListeners();
  }

  Future<bool> retainResolution(NotebookDraft? value) async {
    resolution = value;
    _markDirty();
    return flush();
  }

  /// Called only after the encrypted draft/queue transaction commits.
  void installResolved(LocalInkDraft value) {
    if (value.key != key ||
        !value.document.matches(key.patientId, key.pageId)) {
      throw const FormatException('Resolution binding mismatch');
    }
    loading = true;
    ink.restore(value.document);
    loading = false;
    revision = value.serverRevision;
    rowVersion = value.serverRowVersion;
    syncState = value.syncState;
    resolution = null;
    submission = null;
    queuedCount = 0;
    lastSavedAt = value.updatedAt;
    _generation++;
    dirty = false;
    saved = hasPersisted = true;
    failed = false;
    _debounce?.cancel();
    _checkpoint?.cancel();
    _checkpoint = null;
    notifyListeners();
  }

  Future<bool> acceptQueued(NotebookPage page, NotebookDraft envelope) async {
    final compared = ink.document;
    final bytes = await compute(encodeServerInk, compared);
    final previousRevision = revision, previousVersion = rowVersion;
    revision = page.revision;
    rowVersion = page.rowVersion;
    syncState =
        identical(compared, ink.document) && listEquals(bytes, envelope.bytes)
        ? 'serverSynced'
        : 'localOnly';
    if (submission?.clientDraftId == envelope.clientDraftId) submission = null;
    _markDirty();
    if (await flush()) return true;
    revision = previousRevision;
    rowVersion = previousVersion;
    syncState = 'offline';
    _markDirty();
    return false;
  }

  Future<bool> _write(LocalInkDraft draft, int generation) async {
    try {
      await store.write(draft);
      lastSavedAt = draft.updatedAt;
      hasPersisted = true;
      if (generation == _generation) {
        dirty = false;
        saved = true;
        _checkpoint?.cancel();
        _checkpoint = null;
      }
      failed = false;
      notifyListeners();
      return true;
    } catch (_) {
      failed = true;
      saved = false;
      notifyListeners();
      return false;
    }
  }

  @override
  void dispose() {
    if (disposed) return;
    disposed = true;
    _debounce?.cancel();
    _checkpoint?.cancel();
    ink.removeListener(_edited);
    ink.dispose();
    super.dispose();
  }
}
