import 'package:doctor_tablet/features/notebook/ink/ink_controller.dart';
import 'package:doctor_tablet/features/notebook/ink/ink_document.dart';
import 'package:doctor_tablet/features/notebook/presentation/ink_page.dart';
import 'package:doctor_tablet/l10n/app_localizations.dart';
import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  late InkController ink;
  setUp(() => ink = InkController('patient-a', 'page-a'));
  tearDown(() => ink.dispose());
  void send(
    PointerEvent e, {
    String patient = 'patient-a',
    String page = 'page-a',
    bool enabled = true,
  }) => ink.handle(
    e,
    const Size(420, 594),
    patientId: patient,
    pageId: page,
    enabled: enabled,
  );
  void draw({PointerDeviceKind kind = PointerDeviceKind.stylus}) {
    send(
      PointerDownEvent(
        pointer: 1,
        kind: kind,
        position: const Offset(40, 40),
        pressure: .2,
        pressureMin: 0,
        pressureMax: 1,
      ),
    );
    send(
      PointerMoveEvent(
        pointer: 1,
        kind: kind,
        position: const Offset(200, 200),
        pressure: .8,
        pressureMin: 0,
        pressureMax: 1,
        timeStamp: const Duration(milliseconds: 10),
      ),
    );
    send(PointerUpEvent(pointer: 1, kind: kind));
  }

  test('stylus commits vectors in stable A4 coordinates with timing', () {
    draw();
    final stroke = ink.document.strokes.single;
    expect(stroke.points.last.x, 100);
    expect(stroke.points.last.y, 100);
    expect(stroke.points.last.timeMicros, 10000);
    expect(stroke.usesPressure, isTrue);
    expect(ink.active.value, isNull);
    expect(InkDocument.formatVersion, 1);
    expect(InkStroke.formatVersion, 1);
    expect(InkPoint.formatVersion, 1);
    expect(() => ink.document.strokes.clear(), throwsUnsupportedError);
  });
  test('finger and mouse never draw', () {
    draw(kind: PointerDeviceKind.touch);
    draw(kind: PointerDeviceKind.mouse);
    expect(ink.document.strokes, isEmpty);
    expect(ink.active.value, isNull);
  });
  test(
    'finger contacts cannot interrupt active stylus; moves only notify overlay',
    () {
      var commits = 0, overlays = 0;
      ink.addListener(() => commits++);
      ink.active.addListener(() => overlays++);
      send(
        const PointerDownEvent(
          pointer: 1,
          kind: PointerDeviceKind.stylus,
          position: Offset(40, 40),
        ),
      );
      send(const PointerDownEvent(pointer: 2, kind: PointerDeviceKind.touch));
      send(const PointerMoveEvent(pointer: 2, kind: PointerDeviceKind.touch));
      send(const PointerUpEvent(pointer: 2, kind: PointerDeviceKind.touch));
      send(
        const PointerMoveEvent(
          pointer: 1,
          kind: PointerDeviceKind.stylus,
          position: Offset(80, 80),
        ),
      );
      expect(ink.active.value!.points.length, 2);
      expect(commits, 0);
      expect(overlays, 2);
      send(const PointerUpEvent(pointer: 1, kind: PointerDeviceKind.stylus));
      expect(commits, 1);
    },
  );
  test('binding mismatch and disabled input reject ink', () {
    for (final pair in [('patient-b', 'page-a'), ('patient-a', 'page-b')]) {
      send(
        const PointerDownEvent(kind: PointerDeviceKind.stylus),
        patient: pair.$1,
        page: pair.$2,
      );
      expect(ink.active.value, isNull);
      expect(ink.document.matches(pair.$1, pair.$2), isFalse);
    }
    send(
      const PointerDownEvent(kind: PointerDeviceKind.stylus),
      enabled: false,
    );
    expect(ink.document.strokes, isEmpty);
    expect(ink.active.value, isNull);
  });
  test('normalizes pressure and rejects invalid ranges and samples', () {
    expect(InkPoint.normalizePressure(512, 0, 1024), .5);
    expect(InkPoint.normalizePressure(0, 0, 1), 0);
    expect(InkPoint.normalizePressure(1, 0, 1), 1);
    for (final values in [
      (1.0, 1.0, 1.0),
      (double.nan, 0.0, 1.0),
      (double.infinity, 0.0, 1.0),
      (2.0, 0.0, 1.0),
      (-1.0, 0.0, 1.0),
      (0.5, 1.0, 0.0),
    ]) {
      expect(
        InkPoint.normalizePressure(values.$1, values.$2, values.$3),
        isNull,
      );
    }
    InkStroke stroke(List<double?> p) => InkStroke(
      id: 0,
      color: 0xff000000,
      width: .7,
      points: p.map((v) => InkPoint(x: 0, y: 0, timeMicros: 0, pressure: v)),
    );
    expect(stroke([1, 1]).usesPressure, isFalse);
    expect(stroke([.2, null, .8]).usesPressure, isFalse);
    expect(stroke([.5, .51]).usesPressure, isFalse);
    expect(stroke([.2, .8]).usesPressure, isTrue);
  });
  test('swept eraser removes entire intersected stroke; undo redo restore exact vectors', () {
    draw();
    final original = ink.document.strokes.single;
    ink.tool = InkTool.eraser;
    send(
      const PointerDownEvent(
        pointer: 2,
        kind: PointerDeviceKind.stylus,
        position: Offset(40, 200),
      ),
    );
    send(
      const PointerMoveEvent(
        pointer: 2,
        kind: PointerDeviceKind.stylus,
        position: Offset(200, 40),
      ),
    );
    send(
      const PointerUpEvent(
        pointer: 2,
        kind: PointerDeviceKind.stylus,
        position: Offset(200, 40),
      ),
    );
    expect(ink.document.strokes, isEmpty);
    ink.undo();
    expect(ink.document.strokes.single, same(original));
    ink.redo();
    expect(ink.document.strokes, isEmpty);
    ink.undo();
    ink.undo();
    expect(ink.document.strokes, isEmpty);
    ink.redo();
    expect(ink.document.strokes.single, same(original));
  });
  test('new edit clears redo, cancellation discards active stroke', () {
    draw();
    ink.undo();
    draw();
    expect(ink.canRedo, isFalse);
    send(const PointerDownEvent(pointer: 3, kind: PointerDeviceKind.stylus));
    send(const PointerCancelEvent(pointer: 3, kind: PointerDeviceKind.stylus));
    expect(ink.active.value, isNull);
    expect(ink.document.strokes.length, 1);
  });
  test(
    'page and patient switches discard strokes, active input and history',
    () {
      for (final target in [('patient-a', 'page-b'), ('patient-b', 'page-a')]) {
        ink.bind('patient-a', 'page-a');
        draw();
        send(
          const PointerDownEvent(pointer: 5, kind: PointerDeviceKind.stylus),
        );
        ink.bind(target.$1, target.$2);
        expect(ink.document.strokes, isEmpty);
        expect(ink.active.value, isNull);
        expect(ink.canUndo, isFalse);
        expect(ink.canRedo, isFalse);
        ink.undo();
        ink.redo();
        ink.bind('patient-a', 'page-a');
        expect(ink.document.strokes, isEmpty);
      }
    },
  );

  testWidgets(
    'Listener routes stylus only, preserves committed painter during moves, switches safely',
    (tester) async {
      Future<void> page(String patient, String page, {bool current = true}) =>
          tester.pumpWidget(
            MaterialApp(
              localizationsDelegates: AppLocalizations.localizationsDelegates,
              supportedLocales: AppLocalizations.supportedLocales,
              home: Scaffold(
                body: SingleChildScrollView(
                  child: SizedBox(
                    width: 420,
                    child: InkPage(
                      patientId: patient,
                      pageId: page,
                      enabled: true,
                      isCurrent: () => current,
                    ),
                  ),
                ),
              ),
            ),
          );
      InkPainter painter(String key) =>
          tester.widget<CustomPaint>(find.byKey(Key(key))).painter!
              as InkPainter;
      await page('a', 'one');
      final origin = tester.getTopLeft(find.byKey(const Key('ink-input')));
      final touch = await tester.startGesture(origin + const Offset(30, 30));
      await touch.up();
      await tester.pump();
      expect(painter('ink-committed').strokes, isEmpty);
      // Canvas contacts must not move the surrounding scrolling notebook.
      final initialOrigin = tester.getTopLeft(
        find.byKey(const Key('ink-input')),
      );
      final pen = await tester.startGesture(
        origin + const Offset(30, 30),
        kind: PointerDeviceKind.stylus,
      );
      await tester.pump();
      final committed = painter('ink-committed');
      await pen.moveTo(origin + const Offset(60, 60));
      await tester.pump();
      final activePainter =
          tester
                  .widget<CustomPaint>(find.byKey(const Key('ink-active')))
                  .painter!
              as ActiveInkPainter;
      expect(activePainter.active.points.length, 2);
      expect(painter('ink-committed'), same(committed));
      expect(
        tester.getTopLeft(find.byKey(const Key('ink-input'))),
        initialOrigin,
      );
      await pen.up();
      await tester.pump();
      expect(painter('ink-committed').strokes.length, 1);
      await page('a', 'two');
      expect(painter('ink-committed').strokes, isEmpty);
      final nextPen = await tester.startGesture(
        origin + const Offset(30, 30),
        kind: PointerDeviceKind.stylus,
      );
      await nextPen.up();
      await tester.pump();
      expect(painter('ink-committed').strokes.length, 1);
      await page('b', 'two');
      expect(painter('ink-committed').strokes, isEmpty);
      await page('a', 'two');
      expect(painter('ink-committed').strokes, isEmpty);
      await page('a', 'two', current: false);
      expect(find.byKey(const Key('ink-input')), findsNothing);
    },
  );

  test('eraser misses preserve ink and history; tap erases a dot', () {
    send(
      const PointerDownEvent(
        pointer: 1,
        kind: PointerDeviceKind.stylus,
        position: Offset(40, 40),
      ),
    );
    send(
      const PointerUpEvent(
        pointer: 1,
        kind: PointerDeviceKind.stylus,
        position: Offset(40, 40),
      ),
    );
    final dot = ink.document.strokes.single;
    ink.tool = InkTool.eraser;
    send(
      const PointerDownEvent(
        pointer: 2,
        kind: PointerDeviceKind.stylus,
        position: Offset(300, 300),
      ),
    );
    send(
      const PointerUpEvent(
        pointer: 2,
        kind: PointerDeviceKind.stylus,
        position: Offset(300, 300),
      ),
    );
    expect(ink.document.strokes.single, same(dot));
    send(
      const PointerDownEvent(
        pointer: 3,
        kind: PointerDeviceKind.stylus,
        position: Offset(40, 40),
      ),
    );
    send(
      const PointerUpEvent(
        pointer: 3,
        kind: PointerDeviceKind.stylus,
        position: Offset(40, 40),
      ),
    );
    expect(ink.document.strokes, isEmpty);
    ink.undo();
    expect(ink.document.strokes.single, same(dot));
    ink.undo();
    expect(ink.document.strokes, isEmpty);
  });
}
