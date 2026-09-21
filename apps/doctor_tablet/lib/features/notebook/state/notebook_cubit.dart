import 'dart:async';

import 'package:dio/dio.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../../app/session/session_cubit.dart';
import '../../auth/data/mobile_auth.dart';
import '../../patients/state/patient_context_cubit.dart';
import '../data/notebook_api.dart';

enum NotebookIssue { failed, forbidden, changed, conflict, title }

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
  });
  final String? patientId;
  final List<NotebookPage> items;
  final NotebookPage? selected;
  final int page;
  final bool hasMore, busy, blocked;
  final NotebookIssue? issue;
  final NotebookDraft? pending;
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
  }) => NotebookState(
    patientId: patientId,
    items: items ?? this.items,
    selected: selected ?? this.selected,
    page: page ?? this.page,
    hasMore: hasMore ?? this.hasMore,
    busy: busy,
    issue: issue,
    pending: clearPending ? null : pending ?? this.pending,
    blocked: blocked ?? this.blocked,
  );
}

/// Clinical metadata and retry envelopes live only within one patient/session.
final class NotebookCubit extends Cubit<NotebookState> {
  NotebookCubit(this.session, this.patients, this.api)
    : super(const NotebookState()) {
    _owner = _staff;
    _patientSubscription = patients.stream.listen((_) => _context());
    _sessionSubscription = session.stream.listen((_) => _context());
    _context();
  }
  final SessionCubit session;
  final PatientContextCubit patients;
  final NotebookApi? api;
  final String originDeviceId = notebookIdentifier();
  late final StreamSubscription<PatientContextState> _patientSubscription;
  late final StreamSubscription<SessionState> _sessionSubscription;
  StaffSession? _owner;
  int _generation = 0;
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
      state.pending == null;
  bool get canRevise =>
      canMutate && state.selected != null && !state.selected!.finalized;
  bool get canAmend => canMutate && state.selected?.finalized == true;

  bool ownsPage(String patientId, String pageId) =>
      _current &&
      state.patientId == patientId &&
      state.selected?.patientId == patientId &&
      state.selected?.id == pageId;

  void _context() {
    final id = canRead ? patients.state.active?.patientId : null;
    if (identical(_owner, _staff) && id == state.patientId) return;
    _owner = _staff;
    _generation++;
    _cancel?.cancel();
    emit(NotebookState(patientId: id));
    if (id != null) unawaited(refresh());
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
          items: batch.items,
          selected: selected,
          page: 1,
          hasMore: batch.hasMore,
        ),
      );
    }
  });

  Future<void> loadMore() async {
    if (!state.hasMore || state.pending != null || state.page >= 100) return;
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
            hasMore: batch.hasMore && next < 100,
          ),
        );
      }
    });
  }

  void _accept(NotebookPage page) => emit(
    state.copy(
      selected: page,
      items: List.unmodifiable(
        {for (final p in state.items) p.id: p, page.id: page}.values,
      ),
      clearPending: true,
      blocked: false,
    ),
  );

  Future<void> open(String id) async {
    if (state.pending != null || !state.items.any((p) => p.id == id)) return;
    await _run((cancel, current) async {
      final page = await api!.detail(state.patientId!, id, cancel);
      if (current()) _accept(page);
    });
  }

  Future<void> create(String title) async {
    if (!canMutate) return;
    title = title.trim();
    if (title.isEmpty || title.length > 200) {
      emit(state.copy(issue: NotebookIssue.title));
      return;
    }
    await _run((cancel, current) async {
      final page = await api!.create(state.patientId!, title, cancel);
      if (current()) _accept(page);
    });
  }

  Future<void> submit({bool amendment = false}) async {
    if (amendment ? !canAmend : !canRevise) return;
    emit(
      state.copy(
        pending: NotebookDraft(state.selected!, originDeviceId, amendment),
      ),
    );
    await retry();
  }

  Future<void> retry() async {
    final draft = state.pending;
    if (draft == null || state.blocked || !canWrite) return;
    await _run((cancel, current) async {
      final page = await api!.submit(draft, cancel);
      if (current()) _accept(page);
    });
  }

  Future<void> finalize() async {
    if (!canRevise) return;
    await _run((cancel, current) async {
      final page = await api!.finalize(state.selected!, cancel);
      if (current()) _accept(page);
    });
  }

  @override
  Future<void> close() async {
    _generation++;
    _cancel?.cancel();
    await _patientSubscription.cancel();
    await _sessionSubscription.cancel();
    return super.close();
  }
}
