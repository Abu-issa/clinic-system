import 'dart:convert';
import 'dart:io';

import 'package:doctor_tablet/features/notebook/data/encrypted_draft_store.dart';
import 'package:doctor_tablet/features/notebook/data/local_ink_draft.dart';
import 'package:doctor_tablet/features/notebook/data/notebook_api.dart';
import 'package:doctor_tablet/features/notebook/data/server_ink_codec.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:flutter/material.dart';
import 'package:flutter/scheduler.dart';
import 'package:doctor_tablet/features/notebook/presentation/ink_page.dart';
import 'package:integration_test/integration_test.dart';
import 'package:sqflite_sqlcipher/sqflite.dart' as cipher;

import '../test/stress_fixture.dart';
import 'encrypted_draft_test.dart' show TestKeys;

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();
  testWidgets('synthetic committed page raster timing on emulator', (
    tester,
  ) async {
    final samples = <FrameTiming>[];
    void capture(List<FrameTiming> frames) {
      samples.addAll(frames);
    }

    SchedulerBinding.instance.addTimingsCallback(capture);
    final doc = stressInk();
    try {
      for (var i = 0; i < 60; i++) {
        await tester.pumpWidget(
          MaterialApp(
            home: Center(
              child: Transform.scale(
                scale: 1 + (i % 4) * .1,
                child: SizedBox(
                  width: 600,
                  height: 848,
                  child: RepaintBoundary(
                    child: CustomPaint(painter: InkPainter(doc.strokes)),
                  ),
                ),
              ),
            ),
          ),
        );
        await tester.pump(const Duration(milliseconds: 16));
      }
      await tester.pump(const Duration(seconds: 1));
      final raster =
          samples.map((s) => s.rasterDuration.inMicroseconds).toList()..sort();
      final build = samples.map((s) => s.buildDuration.inMicroseconds).toList()
        ..sort();
      // Diagnostic observation, never a latency SLA or physical hardware result.
      // ignore: avoid_print
      print(
        'FRAMES debug_emulator_samples=${samples.length} raster_p50_us=${raster.isEmpty ? -1 : raster[raster.length ~/ 2]} raster_p95_us=${raster.isEmpty ? -1 : raster[(raster.length * .95).floor()]} build_p95_us=${build.isEmpty ? -1 : build[(build.length * .95).floor()]}',
      );
    } finally {
      SchedulerBinding.instance.removeTimingsCallback(capture);
    }
  });
  testWidgets(
    'SQLCipher synthetic capacity growth, queue backlog, reopen and read-only write failure',
    (tester) async {
      final path =
          '${await cipher.getDatabasesPath()}/phase2c-capacity-test-only.db';
      await cipher.deleteDatabase(path);
      final keys = TestKeys();
      var store = EncryptedDraftStore(keys: keys, path: path);
      const key = DraftKey(
        'synthetic-capacity-owner',
        'synthetic-patient',
        'synthetic-page',
      );
      final document = stressInk();
      final draft = LocalInkDraft(
        key: key,
        document: document,
        updatedAt: DateTime.utc(2026),
      );
      final watch = Stopwatch()..start();
      final before = ProcessInfo.currentRss;
      await store.write(draft);
      final save = watch.elapsedMilliseconds;
      watch.reset();
      final loaded = await store.read(key);
      final load = watch.elapsedMilliseconds;
      expect(loaded!.document.strokes.length, 1000);
      final largeSize = await File(path).length();
      for (var i = 0; i < 50; i++) {
        await store.write(
          LocalInkDraft(
            key: DraftKey(key.owner, key.patientId, 'page-$i'),
            document: stressInk(page: 'page-$i', strokes: 10),
            updatedAt: DateTime.utc(2026),
          ),
        );
      }
      final bytes = encodeServerInk(stressInk(strokes: 10));
      watch.reset();
      for (var i = 0; i < 100; i++) {
        await store.enqueue(
          key,
          NotebookDraft(
            patientId: key.patientId,
            pageId: key.pageId,
            expectedRowVersion: base64Encode(List.filled(8, 0)),
            originDeviceId: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
            amendment: false,
            payload: bytes,
          ),
          i,
        );
      }
      final enqueue = watch.elapsedMilliseconds;
      final growth = await File(path).length();
      await store.close();
      store = EncryptedDraftStore(keys: keys, path: path);
      expect((await store.read(key))!.document.strokes.length, 1000);
      expect(await store.queueInfo(key.owner), hasLength(100));
      expect(await store.summaries(key.owner, key.patientId), hasLength(51));
      expect(await store.summaries('other-owner', key.patientId), isEmpty);
      await store.close();
      // Deterministic OS-backed failure without filling the host/device disk.
      final readonly = await cipher.openDatabase(
        path,
        password: await keys.read(),
        readOnly: true,
        singleInstance: false,
      );
      await expectLater(
        readonly.rawInsert('INSERT INTO drafts (owner) VALUES (?)', [
          'synthetic',
        ]),
        throwsA(isA<cipher.DatabaseException>()),
      );
      await readonly.close();
      store = EncryptedDraftStore(keys: keys, path: path);
      expect(await store.queueInfo(key.owner), hasLength(100));
      expect((await store.read(key))!.document.strokes.length, 1000);
      // Numeric synthetic benchmark only; no identifiers, content, or secrets.
      // ignore: avoid_print
      print(
        'CAPACITY sqlcipher_save_ms=$save load_ms=$load large_db_bytes=$largeSize final_db_bytes=$growth enqueue_100_ms=$enqueue rss_delta_bytes=${ProcessInfo.currentRss - before}',
      );
      await store.close();
      await cipher.deleteDatabase(path);
    },
  );
}
