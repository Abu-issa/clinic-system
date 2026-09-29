import 'dart:ui' as ui;

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:doctor_tablet/features/notebook/ink/committed_ink_cache.dart';
import 'package:doctor_tablet/features/notebook/ink/ink_document.dart';
import 'package:doctor_tablet/features/notebook/ink/ink_viewport.dart';
import 'package:doctor_tablet/features/notebook/presentation/ink_page.dart';

import 'stress_fixture.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  Future<void> ready(CommittedInkCache cache) async {
    for (var i = 0; i < 500 && cache.image == null; i++) {
      await Future<void>.delayed(const Duration(milliseconds: 10));
    }
    expect(cache.image, isNotNull);
  }

  CommittedInkCache create() => CommittedInkCache(
    (canvas, size, strokes) => InkPainter(strokes).paint(canvas, size),
  );
  test(
    'clear during build cannot publish old ink or allocate a blank cache',
    () async {
      late CommittedInkCache cache;
      var recordings = 0;
      cache = CommittedInkCache((canvas, size, strokes) {
        recordings++;
        InkPainter(strokes).paint(canvas, size);
        cache.update(const [], size, 1);
      });
      cache.update(stressInk(strokes: 40).strokes, const Size(210, 297), 1);
      await Future<void>.delayed(const Duration(milliseconds: 700));
      expect(recordings, 1);
      expect(cache.image, isNull);
      cache.dispose();
    },
  );
  test('cache recording failure preserves authoritative vectors', () async {
    final doc = stressInk(strokes: 40);
    final cache = CommittedInkCache((canvas, size, strokes) {
      throw StateError('synthetic allocation failure');
    });
    cache.update(doc.strokes, const Size(210, 297), 1);
    await Future<void>.delayed(const Duration(milliseconds: 300));
    expect(cache.image, isNull);
    final recorder = ui.PictureRecorder();
    InkPainter(
      doc.strokes,
      cache: cache,
    ).paint(Canvas(recorder), const Size(210, 297));
    recorder.endRecording().dispose();
    expect(doc.strokes.length, 40);
    cache.dispose();
  });
  test('repeated page display disposal releases each derived image', () async {
    for (var i = 0; i < 6; i++) {
      final cache = create();
      cache.update(
        stressInk(page: 'page-$i', strokes: 40).strokes,
        const Size(210, 297),
        1,
      );
      await ready(cache);
      expect(
        cache.imageBytes,
        lessThanOrEqualTo(CommittedInkCache.maxPixels * 4),
      );
      cache.dispose();
      expect(cache.image, isNull);
      expect(cache.cachedCount, 0);
    }
  });
  test('derived cache has pixel-identical vector output at its native sampling resolution', () async {
    final cache = create();
    final document = stressInk(strokes: 40);
    cache.update(document.strokes, const Size(210, 297), 1);
    await ready(cache);
    final recorder = ui.PictureRecorder();
    InkPainter(document.strokes).paint(Canvas(recorder), const Size(840, 1188));
    final picture = recorder.endRecording(), cached = cache.image!;
    final direct = await picture.toImage(840, 1188);
    expect(
      (await direct.toByteData())!.buffer.asUint8List(),
      (await cached.toByteData())!.buffer.asUint8List(),
    );
    expect(cache.cachedCount, 40);
    expect(cache.maxZoom, 4);
    expect(document.strokes.expand((s) => s.points).length, 2000);
    direct.dispose();
    picture.dispose();
    cache.dispose();
  });
  test('append reuses prefix; undo eraser and page replacement invalidate immediately', () async {
    final cache = create(), document = stressInk(strokes: 40);
    cache.update(document.strokes, const Size(210, 297), 1);
    await ready(cache);
    final original = cache.image;
    final appended = [
      ...document.strokes,
      InkStroke(
        id: 100,
        color: 0xff000000,
        width: .7,
        points: const [InkPoint(x: 1, y: 2, timeMicros: 0)],
      ),
    ];
    cache.update(appended, const Size(210, 297), 1);
    expect(cache.image, same(original));
    expect(cache.cachedCount, 40);
    cache.update(document.strokes.sublist(1), const Size(210, 297), 1);
    expect(cache.image, isNull);
    expect(cache.cachedCount, 0);
    cache.update(
      stressInk(page: 'different', strokes: 40).strokes,
      const Size(210, 297),
      1,
    );
    expect(cache.image, isNull);
    cache.dispose();
    await Future<void>.delayed(const Duration(milliseconds: 250));
    expect(cache.image, isNull);
  });
  test(
    'viewport changes preserve cache and high zoom uses exact vector fallback',
    () async {
      final cache = create(),
          doc = stressInk(strokes: 40),
          view = InkViewport();
      cache.update(doc.strokes, const Size(210, 297), 1);
      await ready(cache);
      final image = cache.image;
      view.zoom = 5;
      final recA = ui.PictureRecorder(), recB = ui.PictureRecorder();
      InkPainter(
        doc.strokes,
        cache: cache,
        viewport: view,
      ).paint(Canvas(recA), const Size(210, 297));
      InkPainter(doc.strokes).paint(Canvas(recB), const Size(210, 297));
      final pa = recA.endRecording(), pb = recB.endRecording();
      final a = await pa.toImage(210, 297), b = await pb.toImage(210, 297);
      expect(
        (await a.toByteData())!.buffer.asUint8List(),
        (await b.toByteData())!.buffer.asUint8List(),
      );
      expect(cache.image, same(image));
      expect(doc.strokes.length, 40);
      a.dispose();
      b.dispose();
      pa.dispose();
      pb.dispose();
      view.dispose();
      cache.dispose();
    },
  );
  test(
    'dispose during image creation cannot publish stale page output',
    () async {
      late CommittedInkCache cache;
      cache = CommittedInkCache((canvas, size, strokes) {
        InkPainter(strokes).paint(canvas, size);
        cache.dispose();
      });
      cache.update(stressInk(strokes: 40).strokes, const Size(210, 297), 1);
      await Future<void>.delayed(const Duration(milliseconds: 500));
      expect(cache.image, isNull);
    },
  );
}
