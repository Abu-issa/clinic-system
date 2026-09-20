import 'dart:async';

import 'package:dio/dio.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../../app/session/session_cubit.dart';
import '../../auth/data/mobile_auth.dart';
import '../data/patient_search.dart';

enum PatientSearchIssue { invalidTerm, forbidden, unauthorized, network }

final class PatientContextState {
  const PatientContextState({
    this.active,
    this.items = const [],
    this.term = '',
    this.page = 0,
    this.hasMore = false,
    this.busy = false,
    this.searched = false,
    this.issue,
  });
  final PatientContext? active;
  final List<PatientContext> items;
  final String term;
  final int page;
  final bool hasMore;
  final bool busy;
  final bool searched;
  final PatientSearchIssue? issue;
}

/// In-memory and scoped to the authenticated session, not the device or staff ID.
final class PatientContextCubit extends Cubit<PatientContextState> {
  PatientContextCubit(this.session, this.searchApi)
    : super(const PatientContextState()) {
    _owner = session.state is SessionAuthenticated
        ? (session.state as SessionAuthenticated).staff
        : null;
    _subscription = session.stream.listen((next) {
      if (next is SessionRefreshing) {
        return; // Same session during token rotation.
      }
      if (next is SessionAuthenticated) {
        if (!identical(_owner, next.staff)) clear();
        _owner = next.staff;
      } else {
        _owner = null;
        clear();
      }
    });
  }
  final SessionCubit session;
  final PatientSearch? searchApi;
  late final StreamSubscription<SessionState> _subscription;
  StaffSession? _owner;
  int _generation = 0;
  CancelToken? _cancel;
  bool get _authorized =>
      _owner != null &&
      (session.state is SessionAuthenticated ||
          session.state is SessionRefreshing);

  void clear() {
    _generation++;
    _cancel?.cancel();
    if (!isClosed) emit(const PatientContextState());
  }

  Future<void> search(String input) async {
    if (!_authorized || searchApi == null) return;
    final term = input.trim();
    _generation++;
    _cancel?.cancel();
    if (term.length < 2 ||
        term.length > 100 ||
        RegExp(r'[\x00-\x1f\x7f]').hasMatch(term)) {
      emit(
        PatientContextState(
          active: state.active,
          issue: PatientSearchIssue.invalidTerm,
        ),
      );
      return;
    }
    emit(PatientContextState(active: state.active, term: term));
    await _fetch(1);
  }

  Future<void> loadMore() async {
    if (!_authorized ||
        state.busy ||
        !state.hasMore ||
        state.page >= PatientSearch.maxPage) {
      return;
    }
    await _fetch(state.page + 1);
  }

  Future<void> _fetch(int page) async {
    final generation = _generation;
    final previous = state;
    final cancel = _cancel = CancelToken();
    emit(
      PatientContextState(
        active: previous.active,
        items: previous.items,
        term: previous.term,
        page: previous.page,
        hasMore: previous.hasMore,
        searched: previous.searched,
        busy: true,
      ),
    );
    try {
      final result = await searchApi!.search(previous.term, page, cancel);
      if (isClosed || generation != _generation || !_authorized) return;
      final items = <String, PatientContext>{
        for (final p in previous.items) p.patientId: p,
        for (final p in result.items) p.patientId: p,
      };
      emit(
        PatientContextState(
          active: state.active,
          items: List.unmodifiable(items.values),
          term: previous.term,
          page: page,
          hasMore: result.hasMore && page < PatientSearch.maxPage,
          searched: true,
        ),
      );
    } catch (e) {
      if (isClosed ||
          generation != _generation ||
          !_authorized ||
          (e is DioException && CancelToken.isCancel(e))) {
        return;
      }
      final status = e is DioException ? e.response?.statusCode : null;
      final issue = switch (status) {
        401 => PatientSearchIssue.unauthorized,
        403 => PatientSearchIssue.forbidden,
        400 => PatientSearchIssue.invalidTerm,
        _ => PatientSearchIssue.network,
      };
      final denied = status == 401 || status == 403;
      emit(
        PatientContextState(
          active: denied ? null : state.active,
          items: denied ? [] : previous.items,
          term: previous.term,
          page: previous.page,
          hasMore: !denied && previous.hasMore,
          searched: previous.searched,
          issue: issue,
        ),
      );
    }
  }

  // Called only after a deliberate select/confirmed switch in the UI.
  void select(PatientContext patient) {
    if (!_authorized || !state.items.contains(patient)) return;
    emit(
      PatientContextState(
        active: patient,
        items: state.items,
        term: state.term,
        page: state.page,
        hasMore: state.hasMore,
        busy: state.busy,
        searched: state.searched,
        issue: state.issue,
      ),
    );
  }

  @override
  Future<void> close() async {
    _generation++;
    _cancel?.cancel();
    await _subscription.cancel();
    return super.close();
  }
}
