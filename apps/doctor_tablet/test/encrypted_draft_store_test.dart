import 'dart:convert';
import 'dart:io';

import 'package:doctor_tablet/features/notebook/data/encrypted_draft_store.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';

import 'local_drafts_test.dart' show binding, sample;

class MemoryKeys implements DraftKeyStore {
  String? value;
  int writes = 0;
  bool fail = false;
  @override
  Future<String?> read() async => value;
  @override
  Future<void> write(String value) async {
    if (fail) throw StateError('Key storage failure');
    writes++;
    this.value = value;
  }
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  const channel = MethodChannel('com.davidmartos96.sqflite_sqlcipher');
  final calls = <MethodCall>[];
  var exists = false, cipherPresent = true;
  setUp(() {
    calls.clear();
    exists = false;
    cipherPresent = true;
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(channel, (call) async {
          calls.add(call);
          final args = call.arguments as Map? ?? {};
          switch (call.method) {
            case 'databaseExists':
              return exists;
            case 'getDatabasesPath':
              return '/test-only';
            case 'openDatabase':
              return 1;
            case 'query':
              if (args['sql'] == 'PRAGMA cipher_version') {
                return cipherPresent
                    ? [
                        {'cipher_version': '4.10.0'},
                      ]
                    : [];
              }
              if (args['sql'] == 'PRAGMA user_version') {
                return [
                  {'user_version': 1},
                ];
              }
              return [];
            case 'insert':
              return 1;
            case 'execute':
            case 'closeDatabase':
              return null;
            default:
              throw StateError('Unexpected database method');
          }
        });
  });
  tearDown(
    () => TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(channel, null),
  );
  test('encrypted save requires securely persisted random key and transactional SQLCipher write', () async {
    final keys = MemoryKeys();
    final store = EncryptedDraftStore(keys: keys, path: '/test-only/drafts.db');
    await store.write(sample());
    expect(keys.writes, 1);
    expect(base64Url.decode(keys.value!).length, 32);
    final opened =
        calls.firstWhere((c) => c.method == 'openDatabase').arguments as Map;
    expect(opened['password'], keys.value);
    expect(calls.where((c) => c.method == 'insert').length, 1);
    final sql = calls
        .where((c) => c.method == 'execute')
        .map((c) => (c.arguments as Map)['sql'])
        .toList();
    expect(sql, contains('PRAGMA temp_store = MEMORY'));
    expect(sql, contains('COMMIT'));
    await store.close();
    final reopened = EncryptedDraftStore(
      keys: keys,
      path: '/test-only/drafts.db',
    );
    exists = true;
    await reopened.read(binding);
    expect(keys.writes, 1);
    await reopened.close();
  });
  test(
    'missing key on existing database fails without replacement or deletion',
    () async {
      exists = true;
      final keys = MemoryKeys();
      final directory = Directory.systemTemp.createTempSync('cipher-key-test-');
      final file = File('${directory.path}/existing.db')
        ..writeAsBytesSync([0, 1, 2]);
      addTearDown(() {
        file.deleteSync();
        directory.deleteSync();
      });
      final store = EncryptedDraftStore(keys: keys, path: file.path);
      await expectLater(store.read(binding), throwsStateError);
      expect(keys.writes, 0);
      expect(
        calls.any(
          (c) => c.method == 'openDatabase' || c.method == 'deleteDatabase',
        ),
        isFalse,
      );
    },
  );
  test('secure key failure never opens a database', () async {
    final keys = MemoryKeys()..fail = true;
    final store = EncryptedDraftStore(
      keys: keys,
      path: '/test-only/key-failure.db',
    );
    await expectLater(store.write(sample()), throwsStateError);
    expect(calls.any((c) => c.method == 'openDatabase'), isFalse);
  });
  test(
    'unencrypted SQLite engine is rejected without inserting clinical data',
    () async {
      cipherPresent = false;
      final store = EncryptedDraftStore(
        keys: MemoryKeys(),
        path: '/test-only/plain.db',
      );
      await expectLater(store.write(sample()), throwsA(anything));
      expect(calls.any((c) => c.method == 'insert'), isFalse);
    },
  );
  test('draft secure storage disables destructive key reset and isolates auth storage', () {
    final options = SecureDraftKeyStore.storage.aOptions.toMap();
    expect(options['resetOnError'], 'false');
    expect(options['storageNamespace'], 'clinic_notebook_keys_v1');
  });
}
