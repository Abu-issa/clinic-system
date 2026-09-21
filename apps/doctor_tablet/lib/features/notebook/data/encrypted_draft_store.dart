import 'dart:convert';
import 'dart:math';
import 'package:flutter/foundation.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:sqflite_sqlcipher/sqflite.dart' as cipher;
import 'local_ink_draft.dart';

abstract interface class DraftStore {
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
    aOptions: AndroidOptions(resetOnError: false, storageNamespace: 'clinic_notebook_keys_v1'),
    iOptions: IOSOptions(accessibility: KeychainAccessibility.first_unlock_this_device),
  );
  static const name = 'clinic.notebook.database.key.v1';
  @override
  Future<String?> read() => storage.read(key: name);
  @override
  Future<void> write(String value) => storage.write(key: name, value: value);
}

/// SQLCipher is mandatory: no unencrypted SQLite or file fallback.
final class EncryptedDraftStore implements DraftStore {
  EncryptedDraftStore({this.keys = const SecureDraftKeyStore(), this.path});
  final DraftKeyStore keys;
  final String? path;
  Future<cipher.Database>? _opening;
  Future<cipher.Database> _db() async {
    final existing = _opening;
    if (existing != null) return existing;
    final opening = _open();
    _opening = opening;
    try { return await opening; } catch (_) { _opening = null; rethrow; }
  }
  Future<cipher.Database> _open() async {
    final location = path ?? '${await cipher.getDatabasesPath()}/notebook-drafts-v1.db';
    final exists = await cipher.databaseExists(location);
    var key = await keys.read();
    if (key == null) {
      // Never replace a lost key or silently destroy the existing database.
      if (exists) throw StateError('Draft key unavailable');
      final random = Random.secure();
      key = base64UrlEncode(List.generate(32, (_) => random.nextInt(256)));
      await keys.write(key);
      if (await keys.read() != key) throw StateError('Draft key persistence failed');
    }
    if (base64Url.decode(key).length != 32) throw StateError('Invalid draft key');
    return cipher.openDatabase(location, password: key, version: 1,
      singleInstance: false,
      onConfigure: (db) async {
        final version = await db.rawQuery('PRAGMA cipher_version');
        if (version.isEmpty || version.first.values.first.toString().isEmpty) {
          throw StateError('SQLCipher required');
        }
        await db.execute('PRAGMA cipher_memory_security = ON');
        await db.execute('PRAGMA temp_store = MEMORY');
        await db.execute('PRAGMA synchronous = FULL');
      },
      onCreate: (db, _) => db.execute('''CREATE TABLE drafts (
        owner TEXT NOT NULL, patientId TEXT NOT NULL, pageId TEXT NOT NULL,
        formatVersion INTEGER NOT NULL, payload TEXT NOT NULL,
        serverRevision INTEGER, serverRowVersion TEXT,
        updatedAt TEXT NOT NULL, syncState TEXT NOT NULL,
        PRIMARY KEY (owner, patientId, pageId))'''),
    );
  }
  @override
  Future<LocalInkDraft?> read(DraftKey key) async {
    final db = await _db();
    final rows = await db.query('drafts', where: 'owner = ? AND patientId = ? AND pageId = ?',
      whereArgs: [key.owner, key.patientId, key.pageId], limit: 1);
    if (rows.isEmpty) return null;
    return compute(decodeLocalDraft, (key, rows.single));
  }
  @override
  Future<void> write(LocalInkDraft draft) async {
    final db = await _db();
    final row = await compute(encodeLocalDraft, draft);
    await db.transaction((tx) async {
      await tx.insert('drafts', row, conflictAlgorithm: cipher.ConflictAlgorithm.replace);
    });
  }
  @override
  Future<void> close() async {
    final opening = _opening;
    if (opening != null) {
      try { await (await opening).close(); } finally { _opening = null; }
    }
  }
}
