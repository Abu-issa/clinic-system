import 'dart:convert';
import 'dart:math';

import 'package:flutter/foundation.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:sqflite_sqlcipher/sqflite.dart' as cipher;

import 'local_ink_draft.dart';
import 'notebook_api.dart';
import 'sync_queue.dart';

abstract interface class DraftStore {
  Future<Map<String, String>> summaries(String owner, String patientId);
  Future<LocalInkDraft?> read(DraftKey key);
  Future<void> write(LocalInkDraft draft);
  Future<void> close();
}

abstract interface class DraftKeyStore {
  Future<String?> read();
  Future<void> write(String value);
}

final class SecureDraftKeyStore implements DraftKeyStore {
  const SecureDraftKeyStore();
  static const storage = FlutterSecureStorage(
    aOptions: AndroidOptions(
      resetOnError: false,
      storageNamespace: 'clinic_notebook_keys_v1',
    ),
    iOptions: IOSOptions(
      accessibility: KeychainAccessibility.first_unlock_this_device,
    ),
  );
  static const name = 'clinic.notebook.database.key.v1';
  @override
  Future<String?> read() => storage.read(key: name);
  @override
  Future<void> write(String value) => storage.write(key: name, value: value);
}

/// SQLCipher is mandatory: no unencrypted SQLite or file fallback.
final class EncryptedDraftStore implements DraftStore, SyncQueueStore {
  @override
  Future<Map<String, String>> summaries(String owner, String patientId) async {
    final rows = await (await _db()).query(
      'drafts',
      columns: ['pageId', 'syncState'],
      where: 'owner = ? AND patientId = ?',
      whereArgs: [owner, patientId],
    );
    return {
      for (final r in rows)
        r['pageId'] as String: r['syncState'] == 'localOnly'
            ? 'localSaved'
            : r['syncState'] as String,
    };
  }

  @override
  Future<List<QueuedInk>> queueInfo(String owner) async =>
      (await (await _db()).query(
        'SyncQueue',
        columns: queueMetadataColumns,
        where: 'owner = ?',
        whereArgs: [owner],
        orderBy: 'sequence',
      )).map(QueuedInk.new).toList();

  @override
  Future<QueuedInk?> queueItem(String owner, String id) async {
    final rows = await (await _db()).query(
      'SyncQueue',
      where: 'owner = ? AND clientDraftId = ?',
      whereArgs: [owner, id],
      limit: 1,
    );
    return rows.isEmpty ? null : QueuedInk(rows.single);
  }

  EncryptedDraftStore({this.keys = const SecureDraftKeyStore(), this.path});
  final DraftKeyStore keys;
  final String? path;
  Future<cipher.Database>? _opening;
  Future<cipher.Database> _db() async {
    final existing = _opening;
    if (existing != null) return existing;
    final opening = _open();
    _opening = opening;
    try {
      return await opening;
    } catch (_) {
      _opening = null;
      rethrow;
    }
  }

  Future<cipher.Database> _open() async {
    final location =
        path ?? '${await cipher.getDatabasesPath()}/notebook-drafts-v1.db';
    final exists = await cipher.databaseExists(location);
    var key = await keys.read();
    if (key == null) {
      // Never replace a lost key or silently destroy the existing database.
      if (exists) throw StateError('Draft key unavailable');
      final random = Random.secure();
      key = base64UrlEncode(List.generate(32, (_) => random.nextInt(256)));
      await keys.write(key);
      if (await keys.read() != key) {
        throw StateError('Draft key persistence failed');
      }
    }
    if (base64Url.decode(key).length != 32) {
      throw StateError('Invalid draft key');
    }
    return cipher.openDatabase(
      location,
      password: key,
      version: 2,
      singleInstance: false,
      onConfigure: (db) async {
        final version = await db.rawQuery('PRAGMA cipher_version');
        final cipherVersion = version.isEmpty
            ? null
            : version.first.values.first;
        if (cipherVersion is! String || cipherVersion.trim().isEmpty) {
          throw StateError('SQLCipher required');
        }
        await db.execute('PRAGMA cipher_memory_security = ON');
        await db.execute('PRAGMA temp_store = MEMORY');
        await db.execute('PRAGMA synchronous = FULL');
      },
      onCreate: (db, _) async {
        await db.execute('''CREATE TABLE drafts (
        owner TEXT NOT NULL, patientId TEXT NOT NULL, pageId TEXT NOT NULL,
        formatVersion INTEGER NOT NULL, payload TEXT NOT NULL,
        serverRevision INTEGER, serverRowVersion TEXT,
        updatedAt TEXT NOT NULL, syncState TEXT NOT NULL,
        PRIMARY KEY (owner, patientId, pageId))''');
        await _createQueue(db);
      },
      onUpgrade: (db, old, _) async {
        if (old < 2) await _createQueue(db);
      },
    );
  }

  static Future<void> _createQueue(cipher.Database db) async {
    await db.execute('''CREATE TABLE SyncQueue (
      sequence INTEGER PRIMARY KEY AUTOINCREMENT,
      owner TEXT NOT NULL, patientId TEXT NOT NULL, pageId TEXT NOT NULL,
      operationType TEXT NOT NULL, payload BLOB NOT NULL,
      expectedRowVersion TEXT NOT NULL, resolvedRowVersion TEXT, predecessorId TEXT,
      clientDraftId TEXT NOT NULL, originDeviceId TEXT NOT NULL,
      boundaryName TEXT NOT NULL, amendment INTEGER NOT NULL,
      createdAt INTEGER NOT NULL, attemptCount INTEGER NOT NULL,
      nextAttemptAt INTEGER NOT NULL, state TEXT NOT NULL,
      errorCode TEXT, httpStatus INTEGER,
      UNIQUE(owner, clientDraftId))''');
    await db.execute(
      'CREATE INDEX queue_page_order ON SyncQueue(owner, patientId, pageId, sequence)',
    );
  }

  @override
  Future<List<QueuedInk>> queued(String owner) async =>
      (await (await _db()).query(
        'SyncQueue',
        where: 'owner = ?',
        whereArgs: [owner],
        orderBy: 'sequence',
      )).map(QueuedInk.new).toList();

  @override
  Future<void> enqueue(DraftKey key, NotebookDraft draft, int now) async {
    final row = QueuedInk.create(key, draft, now);
    await (await _db()).transaction((tx) async {
      final persisted = await tx.query(
        'drafts',
        where: 'owner = ? AND patientId = ? AND pageId = ?',
        whereArgs: [key.owner, key.patientId, key.pageId],
      );
      if (persisted.isEmpty) throw StateError('Persist draft before enqueue');
      decodeLocalDraft((key, persisted.single));
      final duplicate = await tx.query(
        'SyncQueue',
        where: 'owner = ? AND clientDraftId = ?',
        whereArgs: [key.owner, draft.clientDraftId],
      );
      if (duplicate.isNotEmpty) {
        final previous = QueuedInk(duplicate.single);
        if (previous.key != key ||
            !listEquals(previous.envelope.bytes, draft.bytes) ||
            previous.row['originDeviceId'] != draft.originDeviceId ||
            previous.row['expectedRowVersion'] != draft.expectedRowVersion ||
            previous.row['amendment'] != row['amendment']) {
          throw StateError('Duplicate identity mismatch');
        }
        return;
      }
      final page = await tx.query(
        'SyncQueue',
        where: 'owner = ? AND patientId = ? AND pageId = ?',
        whereArgs: [key.owner, key.patientId, key.pageId],
        orderBy: 'sequence DESC',
        limit: 1,
      );
      if (page.isNotEmpty) row['predecessorId'] = page.single['clientDraftId'];
      await tx.insert('SyncQueue', row);
    });
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
    await (await _db()).update(
      'SyncQueue',
      {
        'state': state,
        'attemptCount': attempts,
        'nextAttemptAt': due,
        'errorCode': code,
        'httpStatus': status,
      },
      where: 'owner = ? AND sequence = ? AND clientDraftId = ?',
      whereArgs: [item.key.owner, item.sequence, item.id],
    );
  }

  @override
  Future<void> acknowledge(QueuedInk item, String rowVersion) async {
    await (await _db()).transaction((tx) async {
      final page = await tx.query(
        'SyncQueue',
        where: 'owner = ? AND patientId = ? AND pageId = ?',
        whereArgs: [item.key.owner, item.key.patientId, item.key.pageId],
        orderBy: 'sequence',
        limit: 2,
      );
      if (page.isEmpty ||
          page.first['clientDraftId'] != item.id ||
          page.first['sequence'] != item.sequence ||
          !listEquals(
            page.first['payload'] as List<int>,
            item.envelope.bytes,
          )) {
        throw StateError('Queue acknowledgement mismatch');
      }
      if (page.length > 1) {
        if (page[1]['predecessorId'] != item.id ||
            page[1]['attemptCount'] != 0) {
          throw StateError('Invalid queue dependency');
        }
        await tx.update(
          'SyncQueue',
          {'resolvedRowVersion': rowVersion, 'predecessorId': null},
          where: 'sequence = ? AND owner = ?',
          whereArgs: [page[1]['sequence'], item.key.owner],
        );
      }
      await tx.delete(
        'SyncQueue',
        where: 'sequence = ? AND owner = ? AND clientDraftId = ?',
        whereArgs: [item.sequence, item.key.owner, item.id],
      );
    });
  }

  @override
  Future<void> completeResolution(
    LocalInkDraft resolved,
    List<String> expectedIds,
    bool Function() current,
  ) async {
    final key = resolved.key;
    final row = await compute(encodeLocalDraft, resolved);
    await (await _db()).transaction((tx) async {
      final args = [key.owner, key.patientId, key.pageId];
      const where = 'owner = ? AND patientId = ? AND pageId = ?';
      final existing = await tx.query('drafts', where: where, whereArgs: args);
      final items = await tx.query(
        'SyncQueue',
        columns: queueMetadataColumns,
        where: where,
        whereArgs: args,
        orderBy: 'sequence',
      );
      if (!current() ||
          existing.length != 1 ||
          decodeLocalDraft((key, existing.single)).syncState != 'conflict' ||
          !listEquals(
            items.map((r) => r['clientDraftId']).toList(),
            expectedIds,
          ) ||
          (items.isNotEmpty && items.first['state'] != 'conflict') ||
          !['serverSynced', 'localOnly'].contains(resolved.syncState) ||
          resolved.resolution != null) {
        throw StateError('Conflict changed');
      }
      decodeLocalDraft((key, row));
      await tx.insert(
        'drafts',
        row,
        conflictAlgorithm: cipher.ConflictAlgorithm.replace,
      );
      await tx.delete('SyncQueue', where: where, whereArgs: args);
      if (!current()) throw StateError('Resolution context changed');
    });
  }

  @override
  Future<LocalInkDraft?> read(DraftKey key) async {
    final db = await _db();
    final rows = await db.query(
      'drafts',
      where: 'owner = ? AND patientId = ? AND pageId = ?',
      whereArgs: [key.owner, key.patientId, key.pageId],
      limit: 1,
    );
    if (rows.isEmpty) return null;
    return compute(decodeLocalDraft, (key, rows.single));
  }

  @override
  Future<void> write(LocalInkDraft draft) async {
    final db = await _db();
    final row = await compute(encodeLocalDraft, draft);
    await db.transaction((tx) async {
      await tx.insert(
        'drafts',
        row,
        conflictAlgorithm: cipher.ConflictAlgorithm.replace,
      );
    });
  }

  @override
  Future<void> close() async {
    final opening = _opening;
    if (opening != null) {
      try {
        await (await opening).close();
      } finally {
        _opening = null;
      }
    }
  }
}
