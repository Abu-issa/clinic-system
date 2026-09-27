import 'dart:async';

import 'package:dio/dio.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter/foundation.dart';

import '../../../app/session/session_cubit.dart';
import '../../auth/data/mobile_auth.dart';
import '../../patients/state/patient_context_cubit.dart';
import '../data/notebook_api.dart';
import '../data/local_ink_draft.dart';
import 'local_drafts.dart';
import 'notebook_sync_queue.dart';
import '../data/server_ink_codec.dart';

enum NotebookIssue {
  failed,
  forbidden,
  changed,
  conflict,
  title,
  resolutionFailed,
  resolved,
}

enum ConflictChoice { later, discard, saveAsNew }

final class NotebookState {
  const NotebookState({
    this.patientId,
    this.items = const [],
    this.selected,
    this.page = 0,
    this.hasMore = false,
    this.busy = false,
    this.issue,
    this.pending,
    this.blocked = false,
    this.localStates = const {},
  });
  final String? patientId;
  final List<NotebookPage> items;
  final NotebookPage? selected;
  final int page;
  final bool hasMore, busy, blocked;
  final NotebookIssue? issue;
  final NotebookDraft? pending;
  final Map<String, String> localStates;
  NotebookState copy({
    List<NotebookPage>? items,
    NotebookPage? selected,
    int? page,
    bool? hasMore,
    bool busy = false,
    NotebookIssue? issue,
    NotebookDraft? pending,
    bool clearPending = false,
    bool? blocked,
    bool clearSelected = false,
    Map<String, String>? localStates,
  }) => NotebookState(
    patientId: patientId,
    items: items ?? this.items,
    selected: clearSelected ? null : selected ?? this.selected,
    localStates: localStates ?? this.localStates,
    page: page ?? this.page,
    hasMore: hasMore ?? this.hasMore,
    busy: busy,
    issue: issue,
    pending: clearPending ? null : pending ?? this.pending,
    blocked: blocked ?? this.blocked,
  );
}

/// Visible metadata is scoped to the current patient. Durable queue replay
/// is independently scoped to the authenticated server/staff owner.
final class NotebookCubit extends Cubit<NotebookState> {
  NotebookCubit(this.session, this.patients, this.api)
    : super(const NotebookState()) {
    _owner = _staff;
    if (api != null) {
      queue = NotebookSyncQueue(
        session.drafts,
        api!,
        () => _currentOwner,
        onAcknowledged: (page) async {
          if (ownsPage(page.patientId, page.id)) {
            emit(
              state.copy(
                selected: page,
                busy: state.busy,
                issue: state.issue,
                clearPending: activeDraft?.submission == null,
                items: [
                  for (final p in state.items) p.id == page.id ? page : p,
                ],
              ),
            );
          } else if (_current && state.patientId == page.patientId) {
            emit(
              state.copy(
                busy: state.busy,
                issue: state.issue,
                items: [
                  for (final p in state.items) p.id == page.id ? page : p,
                ],
              ),
            );
          }
        },
        onStopped: (key, status, code) {
          if (!ownsPage(key.patientId, key.pageId)) return;
          if (status == 401 || status == 403 || status == 404) {
            final handle = activeDraft;
            if (handle != null) session.drafts.leave(handle);
            _selectedHandle = null;
            emit(
              NotebookState(
                patientId: state.patientId,
                blocked: true,
                issue: NotebookIssue.forbidden,
              ),
            );
          } else {
            emit(
              state.copy(
                blocked: true,
                issue: code == 'page_changed'
                    ? NotebookIssue.changed
                    : NotebookIssue.conflict,
              ),
            );
          }
        },
      );
      queue!.start();
    }
    _patientSubscription = patients.stream.listen((_) => _context());
    _sessionSubscription = session.stream.listen((_) => _context());
    session.drafts.addListener(_localChanged);
    _context();
  }
  final SessionCubit session;
  final PatientContextCubit patients;
  final NotebookApi? api;
  NotebookSyncQueue? queue;
  String? get _currentOwner =>
      !isClosed && canWrite ? session.draftOwner : null;
  final String originDeviceId = notebookIdentifier();
  late final StreamSubscription<PatientContextState> _patientSubscription;
  late final StreamSubscription<SessionState> _sessionSubscription;
  StaffSession? _owner;
  int _generation = 0;
  bool editingAmendment = false;
  NotebookPage? conflictServer;
  DraftHandle? _selectedHandle;
  bool _switching = false;
  bool get canCreate =>
      _current &&
      canWrite &&
      !state.busy &&
      state.issue != NotebookIssue.forbidden;
  int get selectedIndex =>
      state.items.indexWhere((p) => p.id == state.selected?.id);
  bool get hasPrevious => selectedIndex > 0;
  bool get hasNext =>
      selectedIndex >= 0 &&
      (selectedIndex + 1 < state.items.length || state.hasMore);
  Future<void> previous() async {
    if (!state.busy && hasPrevious) {
      await open(state.items[selectedIndex - 1].id);
    }
  }

  Future<void> next() async {
    if (state.busy || !hasNext) return;
    final id = state.selected!.id, generation = _generation;
    if (selectedIndex + 1 == state.items.length) await loadMore();
    if (generation == _generation &&
        state.selected?.id == id &&
        selectedIndex + 1 < state.items.length) {
      await open(state.items[selectedIndex + 1].id);
    }
  }

  void _localChanged() {
    if (!_current) return;
    final statuses = {...state.localStates};
    for (final e in session.drafts.statuses.entries) {
      if (e.key.owner == session.draftOwner &&
          e.key.patientId == state.patientId) {
        statuses[e.key.pageId] = e.value;
      }
    }
    if (!mapEquals(statuses, state.localStates)) {
      emit(
        state.copy(
          localStates: Map.unmodifiable(statuses),
          busy: state.busy,
          issue: state.issue,
        ),
      );
    }
  }

  Future<void> _summaries(bool Function() current) async {
    final owner = session.draftOwner, patient = state.patientId;
    if (owner == null || patient == null) return;
    try {
      final statuses = await session.drafts.store.summaries(owner, patient);
      final items = await queue?.store.queueInfo(owner) ?? [];
      final seen = <String>{};
      for (final q in items) {
        if (q.key.owner == owner &&
            q.key.patientId == patient &&
            seen.add(q.key.pageId)) {
          statuses[q.key.pageId] = q.state == 'failed' ? 'syncFailed' : q.state;
        }
      }
      if (!current()) return;
      emit(
        state.copy(localStates: statuses, busy: state.busy, issue: state.issue),
      );
      _localChanged();
    } catch (_) {
      /* Unknown local status must not imply a server ACK. */
    }
  }

  Future<bool> _releaseSelected() async {
    final handle = activeDraft ?? _selectedHandle;
    if (handle == null || handle.disposed) {
      _selectedHandle = null;
      return true;
    }
    handle.setLocked(true);
    handle.ink.finishActive();
    if (!(handle.failedRestore && !handle.dirty) && !await handle.flush()) {
      if (!handle.disposed) handle.setLocked(false);
      return false;
    }
    await session.drafts.release(handle);
    if (identical(_selectedHandle, handle)) _selectedHandle = null;
    return true;
  }

  DraftHandle? get activeDraft {
    final selected = state.selected, owner = session.draftOwner;
    return selected == null || owner == null
        ? null
        : session.drafts.find(DraftKey(owner, selected.patientId, selected.id));
  }

  DraftHandle _draftFor(NotebookPage page) => session.drafts.open(
    DraftKey(session.draftOwner!, page.patientId, page.id),
    revision: page.revision,
    rowVersion: page.rowVersion,
  );
  CancelToken? _cancel;
  StaffSession? get _staff => switch (session.state) {
    SessionAuthenticated(:final staff) => staff,
    SessionRefreshing(:final staff) => staff,
    _ => null,
  };
  bool get canRead =>
      _staff?.roles.any((r) => r == 'Doctor' || r == 'DoctorAssistant') == true;
  bool get canWrite => _staff?.roles.contains('Doctor') == true;
  bool get _current =>
      !isClosed &&
      identical(_owner, _staff) &&
      canRead &&
      state.patientId != null &&
      state.patientId == patients.state.active?.patientId;
  bool get canMutate =>
      _current &&
      canWrite &&
      !state.busy &&
      !state.blocked &&
      activeDraft?.syncState != 'conflict' &&
      activeDraft?.syncState != 'syncFailed';
  bool get canRevise =>
      canMutate && state.selected != null && !state.selected!.finalized;
  bool get canAmend => canMutate && state.selected?.finalized == true;
  bool get canDraw =>
      _current &&
      !_switching &&
      canWrite &&
      !state.blocked &&
      activeDraft?.syncState != 'conflict' &&
      state.selected != null &&
      (!state.selected!.finalized || editingAmendment);
  bool get canFinalize =>
      canRevise &&
      activeDraft?.serverSynced == true &&
      activeDraft?.rowVersion == state.selected?.rowVersion;
  void beginAmendment() {
    if (!canAmend) return;
    editingAmendment = true;
    emit(state.copy());
  }

  bool ownsPage(String patientId, String pageId) =>
      _current &&
      !_switching &&
      state.patientId == patientId &&
      state.selected?.patientId == patientId &&
      state.selected?.id == pageId;

  void _context() {
    final id = canRead ? patients.state.active?.patientId : null;
    if (identical(_owner, _staff) && id == state.patientId) return;
    final old = _selectedHandle;
    _selectedHandle = null;
    if (old != null && !old.disposed) {
      old.setLocked(true);
      session.drafts.leave(old);
    }
    _switching = false;
    if (!identical(_owner, _staff)) queue?.contextChanged();
    _owner = _staff;
    conflictServer = null;
    editingAmendment = false;
    _generation++;
    _cancel?.cancel();
    emit(NotebookState(patientId: id));
    if (id != null) unawaited(refresh());
    unawaited(queue?.drain());
  }

  Future<void> _run(
    Future<void> Function(CancelToken, bool Function()) action,
  ) async {
    if (!_current || api == null || state.busy) return;
    final generation = _generation;
    final cancel = _cancel = CancelToken();
    bool current() => _current && generation == _generation;
    emit(state.copy(busy: true));
    try {
      await action(cancel, current);
    } catch (error) {
      if (!current() ||
          (error is DioException && CancelToken.isCancel(error))) {
        return;
      }
      final status = error is DioException ? error.response?.statusCode : null;
      _switching = false;
      final body = error is DioException ? error.response?.data : null;
      final code = body is Map ? body['code'] : null;
      final issue = status == 409
          ? (code == 'page_changed'
                ? NotebookIssue.changed
                : NotebookIssue.conflict)
          : status == 403 || status == 401
          ? NotebookIssue.forbidden
          : NotebookIssue.failed;
      if (status == 403 || status == 401) {
        emit(
          NotebookState(
            patientId: state.patientId,
            issue: issue,
            blocked: true,
          ),
        );
      } else {
        emit(state.copy(issue: issue, blocked: state.blocked || status == 409));
      }
    }
  }

  Future<void> refresh() => _run((cancel, current) async {
    final patient = state.patientId!;
    final selectedId = state.selected?.id;
    final batch = await api!.list(patient, 1, cancel);
    if (!current()) return;
    final selected = selectedId == null
        ? null
        : await api!.detail(patient, selectedId, cancel);
    if (current()) {
      emit(
        NotebookState(
          patientId: patient,
          items: List.unmodifiable(
            {
              for (final p in batch.items) p.id: p,
              for (final p in state.items)
                if (batch.items.isNotEmpty &&
                    batch.hasMore &&
                    !batch.items.any((b) => b.id == p.id))
                  p.id: p,
            }.values,
          ),
          localStates: state.localStates,
          selected: selected,
          page: 1,
          hasMore: batch.hasMore,
          pending: activeDraft?.submission,
          blocked: activeDraft?.syncState == 'conflict',
        ),
      );
      await _summaries(current);
    }
  });

  Future<void> loadMore() async {
    if (!state.hasMore) return;
    await _run((cancel, current) async {
      final next = state.page + 1;
      final batch = await api!.list(state.patientId!, next, cancel);
      if (current()) {
        emit(
          state.copy(
            items: List.unmodifiable(
              {
                for (final p in state.items) p.id: p,
                for (final p in batch.items) p.id: p,
              }.values,
            ),
            page: next,
            hasMore: batch.hasMore,
          ),
        );
        await _summaries(current);
      }
    });
  }

  Future<void> _accept(
    NotebookPage page,
    bool Function() current, {
    CancelToken? readInk,
  }) async {
    final draft = _draftFor(page);
    var accepted = false;
    try {
      await draft.ready;
      if (!current()) return;
      if (readInk != null &&
          !draft.hasPersisted &&
          !draft.failedRestore &&
          page.revision > 0) {
        // Only the current server revision of the selected page is hydrated.
        // A local draft always wins; conflict resolution remains explicit.
        final document = await api!.readCurrentInk(page, readInk);
        if (!current()) return;
        final restored = LocalInkDraft(
          key: draft.key,
          document: document,
          updatedAt: DateTime.now().toUtc(),
          serverRevision: page.revision,
          serverRowVersion: page.rowVersion,
          syncState: 'serverSynced',
        );
        await session.drafts.store.write(restored);
        if (!current()) return;
        draft.installResolved(restored);
      }
      await queue?.attach(draft);
      if (!current()) return;
      _selectedHandle = draft;
      accepted = true;
      emit(
        state.copy(
          selected: page,
          items: List.unmodifiable(
            {for (final p in state.items) p.id: p, page.id: page}.values,
          ),
          clearPending: draft.submission == null,
          pending: draft.submission,
          blocked: draft.syncState == 'conflict',
        ),
      );
    } finally {
      if (!accepted && !draft.disposed) session.drafts.leave(draft);
    }
  }

  Future<void> open(String id) async {
    if (!state.items.any((p) => p.id == id) || state.selected?.id == id) return;
    await _run((cancel, current) async {
      _switching = true;
      if (!await _releaseSelected()) {
        _switching = false;
        if (current()) emit(state.copy(issue: NotebookIssue.failed));
        return;
      }
      if (!current()) return;
      emit(
        state.copy(
          clearSelected: true,
          clearPending: true,
          blocked: false,
          busy: true,
        ),
      );
      conflictServer = null;
      final page = await api!.detail(state.patientId!, id, cancel);
      if (current()) {
        _switching = false;
        editingAmendment = false;
        await _accept(page, current, readInk: cancel);
      }
    });
  }

  Future<void> create(String title) async {
    if (!canCreate) return;
    title = title.trim();
    if (title.isEmpty || title.length > 200) {
      emit(state.copy(issue: NotebookIssue.title));
      return;
    }
    await _run((cancel, current) async {
      _switching = true;
      if (!await _releaseSelected()) {
        _switching = false;
        if (current()) emit(state.copy(issue: NotebookIssue.failed));
        return;
      }
      if (!current()) return;
      emit(
        state.copy(
          clearSelected: true,
          clearPending: true,
          blocked: false,
          busy: true,
        ),
      );
      final page = await api!.create(state.patientId!, title, cancel);
      if (current()) {
        _switching = false;
        editingAmendment = false;
        conflictServer = null;
        emit(
          state.copy(
            items: [page, ...state.items.where((p) => p.id != page.id)],
            busy: true,
          ),
        );
        await _accept(page, current);
      }
    });
  }

  Future<void> submit({bool amendment = false}) async {
    if (amendment ? !canAmend : !canRevise) return;
    await _sync(amendment: amendment);
  }

  Future<void> retry() async {
    if (state.blocked || !canWrite) return;
    await queue?.drain(force: true);
  }

  Future<void> _sync({required bool amendment}) async {
    final selected = state.selected;
    if (selected == null) return;
    final draft = _draftFor(selected);
    await _run((cancel, current) async {
      try {
        await queue!.enqueue(
          draft,
          selected,
          originDeviceId,
          amendment: amendment,
        );
        if (current()) await queue!.drain();
        if (current()) {
          if (draft.serverSynced) editingAmendment = false;
          emit(
            state.copy(
              clearPending: true,
              blocked: state.blocked || draft.syncState == 'conflict',
              issue: state.issue,
            ),
          );
        }
      } catch (_) {
        if (current()) emit(state.copy(busy: true, pending: draft.submission));
        rethrow;
      }
    });
  }

  Future<void> finalize() async {
    if (!canFinalize) return;
    final draft = activeDraft!;
    draft.setLocked(true);
    try {
      await _run((cancel, current) async {
        try {
          final page = await api!.finalize(state.selected!, cancel);
          await draft.recordFinalization(page);
          if (current()) await _accept(page, current);
        } on DioException catch (error) {
          if (error.response?.statusCode == 409) await draft.recordConflict();
          rethrow;
        }
      });
    } finally {
      if (!draft.disposed) draft.setLocked(false);
    }
  }

  Future<bool> resolveConflict(
    ConflictChoice choice, {
    bool confirmed = false,
  }) async {
    if (choice == ConflictChoice.later ||
        (choice == ConflictChoice.discard && !confirmed)) {
      return false;
    }
    final page = state.selected, handle = activeDraft;
    if (!_current ||
        !canWrite ||
        state.busy ||
        page == null ||
        handle == null ||
        handle.syncState != 'conflict' ||
        handle.key.owner != session.draftOwner) {
      return false;
    }
    final key = handle.key;
    var succeeded = false;
    handle.setLocked(true);
    handle.setSyncing(true);
    try {
      await _run((cancel, contextCurrent) async {
        bool current() =>
            contextCurrent() &&
            session.draftOwner == key.owner &&
            ownsPage(key.patientId, key.pageId);
        var submitted = false;
        try {
          await handle.ready;
          if (!await handle.flush() || !current()) {
            throw StateError('Local save failed');
          }
          final items = (await queue!.store.queueInfo(key.owner))
              .where((q) => q.key == key)
              .toList();
          if (items.isNotEmpty && items.first.state != 'conflict') {
            throw StateError('Conflict changed');
          }
          final latest = await api!.detail(key.patientId, key.pageId, cancel);
          if (!current()) return;
          conflictServer = latest;
          final snapshot = handle.ink.document;
          var document = snapshot;
          var acknowledged = latest;
          if (choice == ConflictChoice.discard) {
            document = await api!.readCurrentInk(latest, cancel);
          } else {
            final bytes = await compute(encodeServerInk, snapshot);
            if (!current()) return;
            // An uncertain explicit resolution retries its durable identity.
            // A first attempt always gets a NEW ID and the fresh server base.
            final envelope =
                handle.resolution ??
                NotebookDraft(
                  patientId: key.patientId,
                  pageId: key.pageId,
                  expectedRowVersion: latest.rowVersion,
                  originDeviceId: originDeviceId,
                  amendment: latest.finalized,
                  payload: bytes,
                );
            if (!listEquals(bytes, envelope.bytes) ||
                !snapshot.matches(envelope.patientId, envelope.pageId)) {
              throw const FormatException('Resolution snapshot changed');
            }
            if (!await handle.retainResolution(envelope)) {
              throw StateError('Local save failed');
            }
            if (!current()) return;
            submitted = true;
            acknowledged = await api!.submit(envelope, cancel);
          }
          if (!current()) return;
          final resolved = LocalInkDraft(
            key: key,
            document: document,
            updatedAt: DateTime.now().toUtc(),
            serverRevision: acknowledged.revision,
            serverRowVersion: acknowledged.rowVersion,
            syncState: acknowledged.revision == 0
                ? 'localOnly'
                : 'serverSynced',
          );
          await queue!.store.completeResolution(
            resolved,
            items.map((q) => q.id).toList(),
            current,
          );
          handle.installResolved(resolved);
          succeeded = true;
          if (current()) {
            editingAmendment = false;
            conflictServer = null;
            emit(
              state.copy(
                selected: acknowledged,
                items: List.unmodifiable(
                  {
                    for (final p in state.items) p.id: p,
                    acknowledged.id: acknowledged,
                  }.values,
                ),
                issue: NotebookIssue.resolved,
                blocked: false,
                clearPending: true,
              ),
            );
          }
        } catch (error) {
          // Definitive rejection permits another explicitly requested rebase.
          // Timeouts/uncertain ACKs retain the same encrypted retry envelope.
          if (submitted &&
              error is DioException &&
              [400, 403, 404, 409, 422].contains(error.response?.statusCode)) {
            await handle.retainResolution(null);
          }
          if (current()) {
            emit(
              state.copy(issue: NotebookIssue.resolutionFailed, blocked: true),
            );
          }
        }
      });
    } finally {
      handle.setSyncing(false);
      handle.setLocked(false);
      if (!handle.attached) session.drafts.leave(handle);
    }
    return succeeded;
  }

  @override
  Future<void> close() async {
    _generation++;
    _cancel?.cancel();
    session.drafts.removeListener(_localChanged);
    final handle = activeDraft ?? _selectedHandle;
    if (handle != null && !handle.disposed) session.drafts.leave(handle);
    await queue?.close();
    await _patientSubscription.cancel();
    await _sessionSubscription.cancel();
    return super.close();
  }
}
