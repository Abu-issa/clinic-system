import 'dart:convert';
import 'dart:io';

import 'package:doctor_tablet/features/notebook/data/encrypted_draft_store.dart';
import 'package:doctor_tablet/features/notebook/data/local_ink_draft.dart';
import 'package:doctor_tablet/features/notebook/data/notebook_api.dart';
import 'package:doctor_tablet/features/notebook/data/server_ink_codec.dart';
import 'package:doctor_tablet/features/notebook/ink/ink_document.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:sqflite_sqlcipher/sqflite.dart' as cipher;

class TestKeys implements DraftKeyStore {
  static const storage = FlutterSecureStorage(
    aOptions: AndroidOptions(
      resetOnError: false,
      storageNamespace: 'clinic_notebook_integration_test',
    ),
  );
  static const name = 'integration-only-key';
  @override
  Future<String?> read() => storage.read(key: name);
  @override
  Future<void> write(String value) => storage.write(key: name, value: value);
}

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();
  testWidgets(
    'SQLCipher conflict resolution rolls back draft and queue together',
    (tester) async {
      final path =
          '${await cipher.getDatabasesPath()}/phase1b5b-integration-only.db';
      final keys = TestKeys();
      await cipher.deleteDatabase(path);
      await TestKeys.storage.delete(key: TestKeys.name);
      var store = EncryptedDraftStore(keys: keys, path: path);
      const key = DraftKey('resolution-owner', 'patient-one', 'page-one');
      const other = DraftKey('resolution-owner', 'patient-two', 'page-two');
      final base = base64Encode(List.filled(8, 0));
      LocalInkDraft local(
        DraftKey key, {
        String state = 'conflict',
        int revision = 0,
      }) => LocalInkDraft(
        key: key,
        document: InkDocument(patientId: key.patientId, pageId: key.pageId),
        updatedAt: DateTime.utc(2026),
        syncState: state,
        serverRevision: revision,
        serverRowVersion: base,
      );
      NotebookDraft envelope(DraftKey key) => NotebookDraft(
        patientId: key.patientId,
        pageId: key.pageId,
        expectedRowVersion: base,
        originDeviceId: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
        amendment: false,
        payload: encodeServerInk(local(key).document),
      );
      try {
        await store.write(local(key));
        await store.write(local(other, state: 'queued'));
        await store.enqueue(key, envelope(key), 1);
        await store.enqueue(other, envelope(other), 2);
        await store.enqueue(key, envelope(key), 3);
        final before = await store.queued(key.owner);
        // Multi-page browsing and queue scans never fetch payload columns.
        expect(await store.summaries(key.owner, key.patientId), {
          key.pageId: 'conflict',
        });
        expect(
          await store.summaries('different-owner', key.patientId),
          isEmpty,
        );
        final metadata = await store.queueInfo(key.owner);
        expect(metadata.map((q) => q.id), before.map((q) => q.id));
        expect(metadata.every((q) => !q.row.containsKey('payload')), isTrue);
        expect(
          (await store.queueItem(key.owner, before.first.id))!.envelope.bytes,
          before.first.envelope.bytes,
        );
        expect(
          await store.queueItem('different-owner', before.first.id),
          isNull,
        );
        await store.schedule(
          before.first,
          'conflict',
          1,
          1,
          'page_changed',
          409,
        );
        final ids = before.where((q) => q.key == key).map((q) => q.id).toList();
        final resolved = local(key, state: 'serverSynced', revision: 4);
        var checks = 0;
        // Fail after the draft INSERT and queue DELETE, before transaction COMMIT.
        await expectLater(
          store.completeResolution(resolved, ids, () => checks++ == 0),
          throwsStateError,
        );
        await store.close();
        store = EncryptedDraftStore(keys: keys, path: path);
        expect((await store.read(key))!.syncState, 'conflict');
        expect(
          (await store.queued(key.owner)).map((q) => q.id),
          before.map((q) => q.id),
        );
        await expectLater(
          store.completeResolution(resolved, [ids.first], () => true),
          throwsStateError,
        );
        await store.completeResolution(resolved, ids, () => true);
        expect((await store.read(key))!.serverRevision, 4);
        expect((await store.read(key))!.syncState, 'serverSynced');
        final remaining = (await store.queued(key.owner)).single;
        expect(remaining.key, other);
        expect(remaining.id, before[1].id);
        expect(remaining.sequence, before[1].sequence);
        expect(remaining.envelope.bytes, before[1].envelope.bytes);
      } finally {
        await store.close();
        await cipher.deleteDatabase(path);
        await TestKeys.storage.delete(key: TestKeys.name);
      }
    },
  );
  testWidgets(
    'SQLCipher v1 upgrade retains encrypted ordered queue across reopen',
    (tester) async {
      final directory = await cipher.getDatabasesPath();
      final path = '$directory/phase1b5a-integration-only.db';
      final keys = TestKeys();
      await cipher.deleteDatabase(path);
      await keys.write(base64UrlEncode(List.generate(32, (i) => i + 1)));
      final secret = (await keys.read())!;
      // Reproduce the previous on-device schema, then exercise the real upgrade.
      final old = await cipher.openDatabase(
        path,
        password: secret,
        version: 1,
        singleInstance: false,
        onCreate: (db, _) => db.execute('''CREATE TABLE drafts (
        owner TEXT NOT NULL, patientId TEXT NOT NULL, pageId TEXT NOT NULL,
        formatVersion INTEGER NOT NULL, payload TEXT NOT NULL,
        serverRevision INTEGER, serverRowVersion TEXT,
        updatedAt TEXT NOT NULL, syncState TEXT NOT NULL,
        PRIMARY KEY (owner, patientId, pageId))'''),
      );
      await old.close();
      var store = EncryptedDraftStore(keys: keys, path: path);
      const key = DraftKey(
        'queue-synthetic-owner',
        'queue-patient-secret',
        'queue-page',
      );
      final document = InkDocument(
        patientId: key.patientId,
        pageId: key.pageId,
      );
      final draft = LocalInkDraft(
        key: key,
        document: document,
        updatedAt: DateTime.utc(2026),
      );
      NotebookDraft envelope() => NotebookDraft(
        patientId: key.patientId,
        pageId: key.pageId,
        expectedRowVersion: base64Encode(List.filled(8, 0)),
        originDeviceId: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
        amendment: false,
        payload: encodeServerInk(document),
      );
      final first = envelope(), second = envelope();
      try {
        await expectLater(store.enqueue(key, first, 1), throwsStateError);
        await store.write(draft);
        await store.enqueue(key, first, 1);
        await store.enqueue(key, first, 1);
        await store.enqueue(key, second, 2);
        var items = await store.queued(key.owner);
        expect(items.length, 2);
        expect(items.last.row['predecessorId'], first.clientDraftId);
        expect(await store.queued('different-owner'), isEmpty);
        await expectLater(
          store.acknowledge(items.last, base64Encode(List.filled(8, 1))),
          throwsStateError,
        );
        await store.schedule(items.first, 'offline', 2, 123456, 'network', 503);
        await store.close();
        store = EncryptedDraftStore(keys: keys, path: path);
        items = await store.queued(key.owner);
        expect(items.first.attempts, 2);
        expect(items.first.due, 123456);
        expect(items.first.envelope.bytes, first.bytes);
        expect(items.first.id, first.clientDraftId);
        final acknowledgedVersion = base64Encode(List.filled(8, 1));
        await store.acknowledge(items.first, acknowledgedVersion);
        items = await store.queued(key.owner);
        expect(items.single.id, second.clientDraftId);
        expect(items.single.envelope.expectedRowVersion, acknowledgedVersion);
        expect(
          items.single.row['expectedRowVersion'],
          second.expectedRowVersion,
        );
        expect(items.single.row['predecessorId'], isNull);
        expect(items.single.envelope.bytes, second.bytes);
        for (final file in Directory(
          directory,
        ).listSync().whereType<File>().where((f) => f.path.startsWith(path))) {
          final text = latin1.decode(await file.readAsBytes());
          expect(text.contains('SQLite format 3'), isFalse);
          expect(text.contains(key.patientId), isFalse);
          expect(text.contains(first.clientDraftId), isFalse);
          expect(text.contains(secret), isFalse);
        }
        await store.close();
        store = EncryptedDraftStore(keys: keys, path: path);
        expect((await store.queued(key.owner)).single.id, second.clientDraftId);
        expect((await store.read(key))!.document.patientId, key.patientId);
      } finally {
        await store.close();
        await cipher.deleteDatabase(path);
        await TestKeys.storage.delete(key: TestKeys.name);
      }
    },
  );
  testWidgets(
    'real SQLCipher: encrypted bytes, secure key, reopen and wrong-key rejection',
    (tester) async {
      final directory = await cipher.getDatabasesPath();
      final path = '$directory/phase1b3-integration-only.db';
      final keys = TestKeys();
      await cipher.deleteDatabase(path);
      await TestKeys.storage.delete(key: TestKeys.name);
      final store = EncryptedDraftStore(keys: keys, path: path);
      const key = DraftKey(
        'synthetic-owner',
        'synthetic-patient-secret',
        'synthetic-page',
      );
      final draft = LocalInkDraft(
        key: key,
        updatedAt: DateTime.utc(2026, 9, 22),
        serverRevision: 3,
        serverRowVersion: 'row-version',
        document: InkDocument(
          patientId: key.patientId,
          pageId: key.pageId,
          strokes: [
            InkStroke(
              id: 0,
              color: 0xff000000,
              width: .7,
              points: const [
                InkPoint(x: 24, y: 30, timeMicros: 123, pressure: .5),
              ],
            ),
          ],
        ),
      );
      try {
        await store.write(draft);
        final secret = await keys.read();
        expect(secret, isNotNull);
        expect(base64Url.decode(secret!).length, 32);
        // Check every database sidecar too, while the database is open.
        for (final file in Directory(
          directory,
        ).listSync().whereType<File>().where((f) => f.path.startsWith(path))) {
          final bytes = await file.readAsBytes();
          final text = latin1.decode(bytes);
          expect(text.contains('SQLite format 3'), isFalse);
          expect(text.contains(key.patientId), isFalse);
          expect(text.contains('timeMicros'), isFalse);
          expect(text.contains(secret), isFalse);
        }
        await store.close();
        final reopened = EncryptedDraftStore(keys: TestKeys(), path: path);
        final restored = await reopened.read(key);
        expect(restored!.document.strokes.single.points.single.x, 24);
        expect(restored.serverRevision, 3);
        expect(
          await reopened.read(
            const DraftKey(
              'synthetic-owner',
              'other-patient',
              'synthetic-page',
            ),
          ),
          isNull,
        );
        await reopened.close();
        // Removing the secure key must not generate a replacement or erase ink.
        await TestKeys.storage.delete(key: TestKeys.name);
        final lost = EncryptedDraftStore(keys: keys, path: path);
        await expectLater(lost.read(key), throwsStateError);
        expect(await keys.read(), isNull);
        await keys.write(base64UrlEncode(List.filled(32, 7)));
        final wrong = EncryptedDraftStore(keys: keys, path: path);
        await expectLater(wrong.read(key), throwsA(anything));
        await keys.write(secret);
        final recovered = EncryptedDraftStore(keys: keys, path: path);
        expect((await recovered.read(key))!.document.strokes.length, 1);
        await recovered.close();
      } finally {
        await store.close();
        await cipher.deleteDatabase(path);
        await TestKeys.storage.delete(key: TestKeys.name);
      }
    },
  );
}
