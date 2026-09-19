import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:equatable/equatable.dart';

/// Placeholder session state machine for the app shell.
///
/// Phase 1A-1 only knows the three lifecycle states; real staff sign-in with
/// MFA, token storage, and server session validation arrive in a later slice
/// and will replace the transitions without changing the shell contract.
sealed class SessionState extends Equatable {
  const SessionState();

  @override
  List<Object?> get props => [];
}

/// App is initializing (configuration, future secure storage open, …).
final class SessionStarting extends SessionState {
  const SessionStarting();
}

/// No staff session — the placeholder login screen is shown.
final class SessionUnauthenticated extends SessionState {
  const SessionUnauthenticated();
}

/// A session exists — the authenticated shell is shown. Carries no token:
/// real credentials never live in this placeholder state.
final class SessionAuthenticated extends SessionState {
  const SessionAuthenticated();
}

final class SessionCubit extends Cubit<SessionState> {
  SessionCubit({this.startupDelay = const Duration(milliseconds: 400)})
      : super(const SessionStarting());

  /// Configurable so tests can start from a settled session instantly.
  final Duration startupDelay;

  /// Simulated startup work. Real initialization (secure storage, API
  /// configuration validation) replaces this in later slices. With a zero
  /// delay the transition is synchronous, which keeps widget tests
  /// deterministic inside the fake-async test zone.
  Future<void> completeStartup() async {
    if (startupDelay > Duration.zero) {
      await Future<void>.delayed(startupDelay);
    }
    emit(const SessionUnauthenticated());
  }

  /// Placeholder entry into the shell; no authentication happens here.
  void signInPlaceholder() => emit(const SessionAuthenticated());

  /// Returns to the login placeholder. Later phases will clear secure
  /// storage and server session state here.
  void signOutPlaceholder() => emit(const SessionUnauthenticated());
}
