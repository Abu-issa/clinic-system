import 'dart:convert';
import 'dart:typed_data';

import 'local_ink_draft.dart';
import 'notebook_api.dart';

/// An immutable clinical operation. Scheduling fields are separate from bytes.
final class QueuedInk {
  QueuedInk(this.row);
  final Map<String, Object?> row;
  int get sequence => row['sequence'] as int;
  DraftKey get key => DraftKey(
    row['owner'] as String,
    row['patientId'] as String,
    row['pageId'] as String,
  );
  String get id => row['clientDraftId'] as String;
  String get state => row['state'] as String;
  int get attempts => row['attemptCount'] as int;
  int get due => row['nextAttemptAt'] as int;
  NotebookDraft get envelope {
    final result = NotebookDraft.fromMap({
      'patientId': key.patientId,
      'pageId': key.pageId,
      'expectedRowVersion':
          row['resolvedRowVersion'] ?? row['expectedRowVersion'],
      'clientDraftId': id,
      'originDeviceId': row['originDeviceId'],
      'boundaryName': row['boundaryName'],
      'amendment': row['amendment'] == 1,
      'bytes': base64Encode(row['payload'] as List<int>),
    });
    if (key.owner.isEmpty ||
        row['operationType'] != (result.amendment ? 'amendment' : 'revision')) {
      throw const FormatException('Queue binding mismatch');
    }
    return result;
  }

  static Map<String, Object?> create(
    DraftKey key,
    NotebookDraft draft,
    int now,
  ) {
    if (key.owner.isEmpty ||
        key.patientId != draft.patientId ||
        key.pageId != draft.pageId) {
      throw const FormatException('Queue binding mismatch');
    }
    NotebookDraft.fromMap(draft.toMap()); // Validate payload binding too.
    return {
      'owner': key.owner,
      'patientId': key.patientId,
      'pageId': key.pageId,
      'operationType': draft.amendment ? 'amendment' : 'revision',
      'payload': Uint8List.fromList(draft.bytes),
      'expectedRowVersion': draft.expectedRowVersion,
      'resolvedRowVersion': null,
      'predecessorId': null,
      'clientDraftId': draft.clientDraftId,
      'originDeviceId': draft.originDeviceId,
      'boundaryName': draft.boundaryName,
      'amendment': draft.amendment ? 1 : 0,
      'createdAt': now,
      'attemptCount': 0,
      'nextAttemptAt': now,
      'state': 'queued',
      'errorCode': null,
      'httpStatus': null,
    };
  }
}

abstract interface class SyncQueueStore {
  Future<List<QueuedInk>> queueInfo(String owner);
  Future<QueuedInk?> queueItem(String owner, String id);
  Future<List<QueuedInk>> queued(String owner);
  Future<void> enqueue(DraftKey key, NotebookDraft draft, int now);
  Future<void> schedule(
    QueuedInk item,
    String state,
    int attempts,
    int due,
    String? code,
    int? status,
  );

  /// Atomically removes this head only and binds its successor to this ACK.
  Future<void> acknowledge(QueuedInk item, String rowVersion);

  /// Replace one conflicted page's draft and remove only its exact queue set.
  /// Both changes commit atomically, or neither changes.
  Future<void> completeResolution(
    LocalInkDraft resolved,
    List<String> expectedIds,
    bool Function() current,
  );
}

const queueMetadataColumns = [
  'sequence',
  'owner',
  'patientId',
  'pageId',
  'clientDraftId',
  'state',
  'attemptCount',
  'nextAttemptAt',
  'predecessorId',
];
