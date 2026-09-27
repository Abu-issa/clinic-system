import 'dart:async';
import 'dart:math' as math;

import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';

import '../data/local_ink_draft.dart';
import '../data/notebook_api.dart';
import '../data/sync_queue.dart';
import 'local_drafts.dart';

/// Foreground, authenticated drain. Routes come solely from validated queue
/// bindings; the visible patient's selection is never used for replay.
final class NotebookSyncQueue extends ChangeNotifier {
  NotebookSyncQueue(
    this.drafts,
    this.api,
    this.owner, {
    DateTime Function()? clock,
    this.onAcknowledged,
    this.onStopped,
  }) : clock = clock ?? DateTime.now;
  final LocalDrafts drafts;
  final NotebookApi api;
  final String? Function() owner;
  final DateTime Function() clock;
  final Future<void> Function(NotebookPage)? onAcknowledged;
  final void Function(DraftKey, int?, String)? onStopped;
  SyncQueueStore get store => drafts.store as SyncQueueStore;
  Timer? _timer;
  CancelToken? _cancel;
  Future<void>? _flight;
  Future<void>? _enqueueFlight;
  bool foreground = true, stopped = false;
  bool _enqueuing = false;
  String? error;
  int _epoch = 0;
  int get now => clock().toUtc().millisecondsSinceEpoch;

  void start() {
    _timer ??= Timer.periodic(
      const Duration(seconds: 15),
      (_) => unawaited(drain()),
    );
    unawaited(drain());
  }

  void contextChanged() {
    _epoch++;
    _cancel?.cancel();
  }

  void setForeground(bool value) {
    foreground = value;
    if (!value) contextChanged();
    if (value) unawaited(drain());
  }

  Future<void> attach(DraftHandle handle) async {
    if (owner() != handle.key.owner) return;
    final items = (await store.queueInfo(handle.key.owner))
        .where((q) => q.key == handle.key)
        .toList();
    handle.queuedCount = items.length;
    if (items.isNotEmpty) {
      final state = items.first.state;
      await handle.queueState(
        state == 'conflict'
            ? 'conflict'
            : state == 'failed'
            ? 'syncFailed'
            : state == 'offline'
            ? 'offline'
            : 'queued',
      );
    }
  }

  Future<void> enqueue(
    DraftHandle handle,
    NotebookPage page,
    String device, {
    bool amendment = false,
  }) {
    final previous = _enqueueFlight;
    final task = () async {
      try {
        await previous;
      } catch (_) {
        /* Preserve subsequent work. */
      }
      if (!stopped) await _enqueue(handle, page, device, amendment: amendment);
    }();
    _enqueueFlight = task;
    return task.whenComplete(() {
      if (identical(_enqueueFlight, task)) _enqueueFlight = null;
    });
  }

  Future<void> _enqueue(
    DraftHandle handle,
    NotebookPage page,
    String device, {
    required bool amendment,
  }) async {
    await _flight;
    final binding = owner();
    if (stopped ||
        binding == null ||
        handle.key.owner != binding ||
        handle.disposed) {
      return;
    }
    _enqueuing = true;
    try {
      await attach(handle);
      final envelope = await handle.prepareSubmission(
        page,
        device,
        amendment: amendment,
      );
      if (stopped || envelope == null || owner() != binding) return;
      final previous = (await store.queueInfo(binding))
          .where((q) => q.key == handle.key)
          .toList();
      final tail = previous.isEmpty
          ? null
          : await store.queueItem(binding, previous.last.id);
      if (tail == null ||
          !listEquals(tail.envelope.bytes, envelope.bytes) ||
          tail.envelope.amendment != envelope.amendment) {
        await store.enqueue(handle.key, envelope, now);
      }
      handle.queuedCount = (await store.queueInfo(binding))
          .where((q) => q.key == handle.key)
          .length;
      await handle.queueState('queued', clearSubmission: true);
      notifyListeners();
    } finally {
      _enqueuing = false;
    }
  }

  Future<void> drain({bool force = false}) {
    if (_enqueuing) return Future.value();
    if (_flight != null) return _flight!;
    final task = _drain(force);
    _flight = task;
    return task.whenComplete(() => _flight = null);
  }

  Future<void> _drain(bool force) async {
    final binding = owner(), epoch = _epoch;
    if (binding == null || !foreground || stopped) return;
    bool valid() =>
        !stopped && foreground && owner() == binding && epoch == _epoch;
    final cancel = _cancel = CancelToken();
    final visited = <DraftKey>{};
    try {
      // Global serialization also guarantees one in-flight write per page.
      // A drain is bounded to 32 ACKs; subsequent ticks continue the backlog.
      for (var count = 0; count < 32 && valid(); count++) {
        final items = await store.queueInfo(binding);
        if (!valid()) return;
        QueuedInk? selected;
        final seen = <DraftKey>{};
        for (final q in items) {
          if (q.key.owner != binding) {
            throw const FormatException('Queue owner mismatch');
          }
          if (!seen.add(q.key) || visited.contains(q.key)) continue;
          if (q.state == 'conflict' ||
              q.state == 'failed' ||
              q.row['predecessorId'] != null ||
              (!force && q.due > now)) {
            continue;
          }
          selected = q;
          break;
        }
        if (selected == null) break;
        final item = await store.queueItem(binding, selected.id);
        if (item == null || !valid()) continue;
        final handle = drafts.forSync(item.key);
        handle.setSyncing(
          true,
        ); // Hold the handle across restore and page disposal.
        await handle.ready;
        handle.queuedCount = items.where((q) => q.key == item.key).length;
        final attempt = item.attempts + 1;
        try {
          if (handle.failedRestore ||
              !handle.hasPersisted ||
              !handle.ink.document.matches(
                item.key.patientId,
                item.key.pageId,
              )) {
            throw const FormatException('Draft binding mismatch');
          }
          final envelope = item.envelope;
          // Persist attempt before sending; a crash retries this same envelope.
          await store.schedule(item, 'queued', attempt, now, null, null);
          if (!valid()) return;
          final page = await api.submit(envelope, cancel);
          if (!valid()) return;
          if (!await handle.acceptQueued(page, envelope)) {
            throw StateError('Local persistence failed');
          }
          if (!valid()) return;
          await store.acknowledge(item, page.rowVersion);
          handle.queuedCount--;
          if (handle.queuedCount > 0) await handle.queueState('queued');
          if (valid()) await onAcknowledged?.call(page);
        } catch (failure) {
          if (!valid()) return; // Retain exact envelope on pause/logout.
          final status = failure is DioException
              ? failure.response?.statusCode
              : null;
          final transient =
              failure is DioException &&
              (status == null ||
                  status >= 500 ||
                  status == 408 ||
                  status == 429);
          final body = failure is DioException ? failure.response?.data : null;
          final changed =
              status == 409 && body is Map && body['code'] == 'page_changed';
          final state = changed
              ? 'conflict'
              : transient
              ? 'offline'
              : 'failed';
          final delay = math.min(300, 5 * (1 << math.min(attempt - 1, 6)));
          await store.schedule(
            item,
            state,
            attempt,
            now + delay * 1000,
            changed
                ? 'page_changed'
                : transient
                ? 'network'
                : 'sync_failed',
            status,
          );
          await handle.queueState(state == 'failed' ? 'syncFailed' : state);
          if (state == 'failed' || state == 'conflict') {
            onStopped?.call(
              item.key,
              status,
              changed ? 'page_changed' : 'sync_failed',
            );
          }
          visited.add(item.key);
        } finally {
          handle.setSyncing(false);
          if (!handle.attached) drafts.leave(handle);
        }
      }
      error = null;
    } catch (_) {
      error = 'local_storage'; // Never expose exception/auth/body data.
    } finally {
      if (!stopped) notifyListeners();
    }
  }

  Future<void> close() async {
    stopped = true;
    contextChanged();
    _timer?.cancel();
    try {
      await _enqueueFlight;
    } catch (_) {
      /* Draft/envelope remains persisted. */
    }
    await _flight;
    dispose();
  }
}
