import 'dart:async';

import 'package:equatable/equatable.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:clinic_core/clinic_core.dart';

import '../../core/api/api_client_factory.dart';
import '../../features/auth/data/mobile_auth.dart';
import '../../features/auth/data/token_store.dart';

sealed class SessionState extends Equatable {
  const SessionState();
  @override
  List<Object?> get props => [];
}

final class SessionStarting extends SessionState {
  const SessionStarting();
}

final class SessionUnauthenticated extends SessionState {
  const SessionUnauthenticated([this.issue]);
  final AuthIssue? issue;
  @override
  List<Object?> get props => [issue];
}

final class SessionPasswordLoading extends SessionState {
  const SessionPasswordLoading();
}

final class SessionMfaRequired extends SessionState {
  const SessionMfaRequired({this.busy = false, this.issue});
  final bool busy;
  final AuthIssue? issue;
  @override
  List<Object?> get props => [busy, issue];
}

final class SessionAuthenticated extends SessionState {
  const SessionAuthenticated(this.staff);
  final StaffSession staff;
  @override
  List<Object?> get props => [staff];
}

final class SessionRefreshing extends SessionState {
  const SessionRefreshing([this.staff]);
  final StaffSession? staff;
}

final class SessionLoggingOut extends SessionState {
  const SessionLoggingOut();
}

final class SessionExpired extends SessionState {
  const SessionExpired([this.issue = AuthIssue.expired]);
  final AuthIssue issue;
  @override
  List<Object?> get props => [issue];
}

final class SessionCubit extends Cubit<SessionState> {
  SessionCubit(this.auth) : super(const SessionStarting()) {
    _subscription = auth?.events.listen((event) {
      if (isClosed || state is SessionLoggingOut) return;
      switch (event) {
        case AuthEvent.refreshing:
          emit(SessionRefreshing(_staff));
        case AuthEvent.refreshed:
          emit(
            _staff == null
                ? const SessionStarting()
                : SessionAuthenticated(_staff!),
          );
        case AuthEvent.expired:
          _staff = null;
          emit(const SessionExpired());
      }
    });
  }
  factory SessionCubit.configured() {
    const url = String.fromEnvironment('CLINIC_API_URL');
    try {
      final factory = ApiClientFactory(ApiConfig.production(url));
      return SessionCubit(
        MobileAuth(
          transport: factory.create(),
          api: factory.create(),
          store: const SecureTokenStore(),
        ),
      );
    } catch (_) {
      return SessionCubit(null);
    }
  }
  final MobileAuth? auth;
  StreamSubscription<AuthEvent>? _subscription;
  StaffSession? _staff;
  int _operation = 0;

  Future<void> completeStartup() async {
    final operation = ++_operation;
    if (auth == null) {
      emit(const SessionUnauthenticated(AuthIssue.configuration));
      return;
    }
    try {
      final staff = await auth!.restore();
      if (operation != _operation || isClosed) return;
      _staff = staff;
      emit(
        staff == null
            ? const SessionUnauthenticated()
            : SessionAuthenticated(staff),
      );
    } catch (e) {
      if (operation == _operation && !isClosed) emit(SessionExpired(_issue(e)));
    }
  }

  Future<void> login(String login, String password) async {
    if (state is! SessionUnauthenticated && state is! SessionExpired) return;
    if (auth == null) {
      emit(const SessionUnauthenticated(AuthIssue.configuration));
      return;
    }
    final operation = ++_operation;
    emit(const SessionPasswordLoading());
    try {
      await auth!.login(login, password);
      if (operation == _operation && !isClosed) {
        emit(const SessionMfaRequired());
      }
    } catch (e) {
      if (operation == _operation && !isClosed) {
        emit(SessionUnauthenticated(_issue(e)));
      }
    }
  }

  Future<void> verify(String code) async {
    if (state is! SessionMfaRequired || (state as SessionMfaRequired).busy) {
      return;
    }
    final operation = ++_operation;
    emit(const SessionMfaRequired(busy: true));
    try {
      final staff = await auth!.verify(code);
      if (operation != _operation || isClosed) return;
      _staff = staff;
      emit(SessionAuthenticated(staff));
    } catch (e) {
      if (operation != _operation || isClosed) return;
      final issue = _issue(e);
      emit(
        issue == AuthIssue.mfa || issue == AuthIssue.network
            ? SessionMfaRequired(issue: issue)
            : SessionExpired(issue),
      );
    }
  }

  void cancelMfa() {
    _operation++;
    auth?.cancelChallenge();
    emit(const SessionUnauthenticated());
  }

  Future<void> logout() async {
    if (state is SessionLoggingOut) return;
    _operation++;
    emit(const SessionLoggingOut());
    try {
      await auth?.logout();
      if (!isClosed) emit(const SessionUnauthenticated());
    } catch (_) {
      if (!isClosed) emit(const SessionExpired(AuthIssue.storage));
    } finally {
      _staff = null;
    }
  }

  AuthIssue _issue(Object e) => e is AuthFailure ? e.issue : AuthIssue.network;
  @override
  Future<void> close() async {
    _operation++;
    await _subscription?.cancel();
    await auth?.dispose();
    return super.close();
  }
}
