import 'dart:io';
import 'dart:ui' as ui;

import 'package:doctor_tablet/features/notebook/data/encrypted_draft_store.dart';
import 'package:doctor_tablet/features/notebook/data/local_ink_draft.dart';
import 'package:doctor_tablet/features/notebook/data/server_ink_codec.dart';
import 'package:doctor_tablet/features/notebook/ink/ink_controller.dart';
import 'package:sqflite_sqlcipher/sqflite.dart' as cipher;

import 'encrypted_draft_test.dart' show TestKeys;

import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter/scheduler.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:doctor_tablet/features/notebook/presentation/ink_page.dart';

import '../test/stress_fixture.dart';

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();
  testWidgets('same-mode codec storage long-active recording and dense reopen', (
    tester,
  ) async {
    final mode = kReleaseMode
        ? 'release'
        : kProfileMode
        ? 'profile'
        : 'debug';
    final document = stressInk();
    final watch = Stopwatch()..start();
    final bytes = encodeServerInk(document);
    final encode = watch.elapsedMicroseconds;
    watch.reset();
    final decoded = decodeServerInk(bytes, document.patientId, document.pageId);
    final decode = watch.elapsedMicroseconds;
    const key = DraftKey(
      'synthetic-performance-owner',
      'synthetic-patient',
      'synthetic-page',
    );
    final draft = LocalInkDraft(
      key: key,
      document: decoded,
      updatedAt: DateTime.utc(2026),
    );
    watch.reset();
    final json = encodeLocalDraft(draft);
    final localEncode = watch.elapsedMicroseconds;
    watch.reset();
    decodeLocalDraft((key, json));
    final localDecode = watch.elapsedMicroseconds;
    final path =
        '${await cipher.getDatabasesPath()}/phase2d-performance-only.db';
    await cipher.deleteDatabase(path);
    var store = EncryptedDraftStore(keys: TestKeys(), path: path);
    final rss = <int>[];
    try {
      watch.reset();
      await store.write(draft);
      final save = watch.elapsedMilliseconds;
      watch.reset();
      await store.read(key);
      final load = watch.elapsedMilliseconds;
      for (var i = 0; i < 5; i++) {
        await store.close();
        store = EncryptedDraftStore(keys: TestKeys(), path: path);
        expect((await store.read(key))!.document.strokes.length, 1000);
        rss.add(ProcessInfo.currentRss);
      }
      // ignore: avoid_print
      print(
        'CODEC mode=$mode encode_us=$encode decode_us=$decode local_encode_us=$localEncode local_decode_us=$localDecode save_ms=$save load_ms=$load reopen_rss=$rss',
      );
      for (final count in [1000, 10000, 20000]) {
        final active = ActiveInk();
        for (final point in stressInk(
          strokes: 1,
          points: count,
        ).strokes.single.points) {
          active.add(point);
        }
        watch.reset();
        for (var i = 0; i < 10; i++) {
          final recorder = ui.PictureRecorder();
          ActiveInkPainter(active)
              .paint(Canvas(recorder), const Size(600, 848));
          recorder.endRecording().dispose();
        }
        // ignore: avoid_print
        print(
          'ACTIVE mode=$mode points=$count record_10_us=${watch.elapsedMicroseconds}',
        );
        active.dispose();
      }
    } finally {
      await store.close();
      await cipher.deleteDatabase(path);
    }
  });
  testWidgets('paired same-mode dense-page baseline and bounded display cache', (
    tester,
  ) async {
    final doc = stressInk();
    final timings = <FrameTiming>[];
    void collect(List<FrameTiming> value) {
      timings.addAll(value);
    }

    SchedulerBinding.instance.addTimingsCallback(collect);
    final mode = kReleaseMode
        ? 'release'
        : kProfileMode
        ? 'profile'
        : 'debug';
    try {
      for (final cached in [false, true]) {
        final before = ProcessInfo.currentRss;
        Widget surface(double scale, double pan) => MaterialApp(
          home: Center(
            child: Transform.translate(
              offset: Offset(pan, 0),
              child: Transform.scale(
                scale: scale,
                child: SizedBox(
                  width: 600,
                  height: 848,
                  child: RepaintBoundary(
                    child: cached
                        ? CommittedInkView(strokes: doc.strokes)
                        : CustomPaint(painter: InkPainter(doc.strokes)),
                  ),
                ),
              ),
            ),
          ),
        );
        final cold = Stopwatch()..start();
        await tester.pumpWidget(surface(1, 0));
        if (cached) {
          for (var i = 0; i < 600; i++) {
            await tester.pump(const Duration(milliseconds: 100));
            final painter =
                tester
                        .widget<CustomPaint>(
                          find.byKey(const Key('ink-committed')),
                        )
                        .painter!
                    as InkPainter;
            if (painter.cache!.image != null) break;
          }
          final painter =
              tester
                      .widget<CustomPaint>(
                        find.byKey(const Key('ink-committed')),
                      )
                      .painter!
                  as InkPainter;
          expect(painter.cache!.image, isNotNull);
          expect(
            painter.cache!.imageBytes,
            lessThanOrEqualTo(8 * 1024 * 1024 * 4),
          );
        }
        final coldMs = cold.elapsedMilliseconds;
        await tester.pump(const Duration(seconds: 1));
        timings.clear();
        for (var i = 0; i < 20; i++) {
          await tester.pumpWidget(
            surface(1 + (i % 4) * .1, (i % 5).toDouble()),
          );
          await tester.pump(const Duration(milliseconds: 16));
        }
        await tester.pump(const Duration(seconds: 1));
        int percentile(List<int> values, double percent) {
          values.sort();
          return values.isEmpty
              ? -1
              : values[((values.length - 1) * percent).round()];
        }

        final build = timings
            .map((f) => f.buildDuration.inMicroseconds)
            .toList();
        final raster = timings
            .map((f) => f.rasterDuration.inMicroseconds)
            .toList();
        // Nominal 60 Hz reference only, not a physical input latency assertion.
        final slow = timings
            .where(
              (f) =>
                  f.buildDuration.inMicroseconds > 16667 ||
                  f.rasterDuration.inMicroseconds > 16667,
            )
            .length;
        // ignore: avoid_print
        print(
          'ACCEPT mode=$mode cached=$cached samples=${timings.length} cold_ms=$coldMs build_p50_us=${percentile(build, .5)} build_p95_us=${percentile(build, .95)} raster_p50_us=${percentile(raster, .5)} raster_p95_us=${percentile(raster, .95)} over_16_67ms=$slow rss_delta_bytes=${ProcessInfo.currentRss - before}',
        );
        await tester.pumpWidget(const SizedBox());
        await tester.pump();
      }
    } finally {
      SchedulerBinding.instance.removeTimingsCallback(collect);
    }
  }, timeout: const Timeout(Duration(minutes: 8)));
}
