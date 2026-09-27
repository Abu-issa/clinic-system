import 'dart:async';

import 'package:doctor_tablet/features/notebook/data/encrypted_draft_store.dart';
import 'package:doctor_tablet/features/notebook/data/local_ink_draft.dart';
import 'package:doctor_tablet/features/notebook/data/notebook_api.dart';
import 'package:doctor_tablet/features/notebook/data/sync_queue.dart';
import 'package:flutter/foundation.dart';

/// Test-only store, never part of production code or persisted to disk.
class MemoryDraftStore implements DraftStore, SyncQueueStore {
  final reads = <DraftKey>[];
  @override
  Future<Map<String, String>> summaries(String owner, String patientId) async =>
      {
        for (final e in rows.entries)
          if (e.key.owner == owner && e.key.patientId == patientId)
            e.key.pageId: e.value['syncState'] == 'localOnly'
                ? 'localSaved'
                : e.value['syncState'] as String,
      };
  @override
  Future<List<QueuedInk>> queueInfo(String owner) async => (await queued(owner))
      .map(
        (q) => QueuedInk({for (final c in queueMetadataColumns) c: q.row[c]}),
      )
      .toList();
  @override
  Future<QueuedInk?> queueItem(String owner, String id) async {
    final items = (await queued(owner)).where((q) => q.id == id);
    return items.isEmpty ? null : items.single;
  }

  bool failResolution = false;
  Completer<void>? resolutionGate;
  @override
  Future<void> completeResolution(
    LocalInkDraft resolved,
    List<String> expectedIds,
    bool Function() current,
  ) async {
    await resolutionGate?.future;
    if (fail || failResolution) throw StateError('Unavailable');
    final items = (await queued(resolved.key.owner))
        .where((q) => q.key == resolved.key)
        .toList();
    final existing = await read(resolved.key);
    if (!current() ||
        existing?.syncState != 'conflict' ||
        !listEquals(items.map((q) => q.id).toList(), expectedIds) ||
        (items.isNotEmpty && items.first.state != 'conflict')) {
      throw StateError('Conflict changed');
    }
    rows[resolved.key] = encodeLocalDraft(resolved);
    for (final item in items) {
      queueRows.remove(item.sequence);
    }
  }

  final queueRows = <int, Map<String, Object?>>{};
  int sequence = 0;
  @override
  Future<List<QueuedInk>> queued(String owner) async {
    if (fail) throw StateError('Unavailable');
    return queueRows.values
        .where((r) => r['owner'] == owner)
        .map((r) => QueuedInk(Map.of(r)))
        .toList();
  }

  @override
  Future<void> enqueue(DraftKey key, NotebookDraft draft, int now) async {
    if (fail || !rows.containsKey(key)) throw StateError('Unavailable draft');
    final row = QueuedInk.create(key, draft, now);
    final items = await queued(key.owner);
    final duplicate = items.where((q) => q.id == draft.clientDraftId);
    if (duplicate.isNotEmpty) {
      if (duplicate.single.key != key ||
          !listEquals(duplicate.single.envelope.bytes, draft.bytes)) {
        throw StateError('Identity mismatch');
      }
      return;
    }
    final page = items.where((q) => q.key == key);
    if (page.isNotEmpty) row['predecessorId'] = page.last.id;
    row['sequence'] = ++sequence;
    queueRows[sequence] = row;
  }

  @override
  Future<void> schedule(
    QueuedInk item,
    String state,
    int attempts,
    int due,
    String? code,
    int? status,
  ) async {
    if (fail) throw StateError('Unavailable');
    queueRows[item.sequence]!.addAll({
      'state': state,
      'attemptCount': attempts,
      'nextAttemptAt': due,
      'errorCode': code,
      'httpStatus': status,
    });
  }

  @override
  Future<void> acknowledge(QueuedInk item, String rowVersion) async {
    if (fail) throw StateError('Unavailable');
    final page = (await queued(item.key.owner))
        .where((q) => q.key == item.key)
        .toList();
    if (page.isEmpty ||
        page.first.id != item.id ||
        page.first.sequence != item.sequence) {
      throw StateError('ACK mismatch');
    }
    if (page.length > 1) {
      queueRows[page[1].sequence]!.addAll({
        'resolvedRowVersion': rowVersion,
        'predecessorId': null,
      });
    }
    queueRows.remove(item.sequence);
  }

  final rows = <DraftKey, Map<String, Object?>>{};
  int writes = 0;
  bool fail = false;
  Completer<void>? gate;
  @override
  Future<LocalInkDraft?> read(DraftKey key) async {
    reads.add(key);
    if (fail) throw StateError('Unavailable');
    final row = rows[key];
    return row == null ? null : decodeLocalDraft((key, row));
  }

  @override
  Future<void> write(LocalInkDraft draft) async {
    await gate?.future;
    if (fail) throw StateError('Unavailable');
    writes++;
    rows[draft.key] = encodeLocalDraft(draft);
  }

  @override
  Future<void> close() async {}
}
