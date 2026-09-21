import 'package:doctor_tablet/features/notebook/ink/ink_controller.dart';
import 'package:doctor_tablet/features/notebook/ink/ink_viewport.dart';
import 'package:doctor_tablet/features/notebook/presentation/ink_page.dart';
import 'package:doctor_tablet/l10n/app_localizations.dart';
import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  const size = Size(420, 594);
  late InkViewport view;
  setUp(() => view = InkViewport());
  tearDown(() => view.dispose());
  void down(
    int id,
    Offset p, [
    PointerDeviceKind kind = PointerDeviceKind.touch,
  ]) =>
      view.handle(PointerDownEvent(pointer: id, position: p, kind: kind), size);
  void move(
    int id,
    Offset p, [
    PointerDeviceKind kind = PointerDeviceKind.touch,
  ]) =>
      view.handle(PointerMoveEvent(pointer: id, position: p, kind: kind), size);
  void up(int id, [PointerDeviceKind kind = PointerDeviceKind.touch]) =>
      view.handle(PointerUpEvent(pointer: id, kind: kind), size);

  test('one finger pans without changing scale', () {
    down(1, const Offset(100, 100));
    move(1, const Offset(140, 120));
    expect(view.pan, const Offset(40, 20));
    expect(view.zoom, 1);
    up(1);
    move(1, const Offset(200, 200));
    expect(view.pan, const Offset(40, 20));
  });
  test('small viewports retain valid pan bounds', () {
    const tiny = Size(20, 20);
    view.handle(
      const PointerDownEvent(pointer: 1, position: Offset(5, 5)),
      tiny,
    );
    view.handle(
      const PointerMoveEvent(pointer: 1, position: Offset(500, 500)),
      tiny,
    );
    expect(view.pan.dx.isFinite, isTrue);
    expect(view.pan.dy.isFinite, isTrue);
    expect(view.pan.dx, lessThan(20));
  });
  test('two fingers zoom and pan around focal point without rotation', () {
    down(1, const Offset(100, 200));
    down(2, const Offset(200, 200));
    final anchor = view.toPage(const Offset(150, 200), size);
    move(2, const Offset(300, 200));
    expect(view.zoom, 2);
    expect(view.toScreen(anchor, size), const Offset(200, 200));
    up(2);
    final before = view.pan;
    move(1, const Offset(110, 210));
    expect(view.pan, before + const Offset(10, 10));
  });
  test('pinch zoom bounds, finite offsets and reset', () {
    down(1, const Offset(100, 100));
    down(2, const Offset(200, 100));
    move(2, const Offset(10000, 100));
    expect(view.zoom, InkViewport.maxZoom);
    move(2, const Offset(101, 100));
    expect(view.zoom, InkViewport.minZoom);
    expect(view.pan.dx.isFinite, isTrue);
    view.reset();
    expect(view.zoom, 1);
    expect(view.pan, Offset.zero);
    move(1, const Offset(150, 150));
    expect(view.pan, Offset.zero);
  });
  test('screen/page conversion round trips through fit, pan and zoom', () {
    down(1, const Offset(100, 100));
    down(2, const Offset(200, 100));
    move(2, const Offset(250, 130));
    for (final viewport in [size, const Size(900, 600)]) {
      for (final point in [
        Offset.zero,
        const Offset(105, 148.5),
        const Offset(210, 297),
      ]) {
        final actual = view.toPage(view.toScreen(point, viewport), viewport);
        expect(actual.dx, closeTo(point.dx, 1e-8));
        expect(actual.dy, closeTo(point.dy, 1e-8));
      }
    }
  });
  test(
    'stylus suppresses existing and new fingers, fresh touch resumes on up',
    () {
      down(1, const Offset(100, 100));
      down(9, const Offset(80, 80), PointerDeviceKind.stylus);
      move(1, const Offset(200, 200));
      down(2, const Offset(120, 120));
      move(2, const Offset(250, 250));
      expect(view.pan, Offset.zero);
      expect(view.zoom, 1);
      expect(view.stylusActive, isTrue);
      up(9, PointerDeviceKind.stylus);
      // Contacts that overlapped the pen must lift before navigating again.
      move(1, const Offset(300, 300));
      expect(view.pan, Offset.zero);
      up(1);
      up(2);
      down(3, const Offset(100, 100));
      move(3, const Offset(120, 140));
      expect(view.pan, const Offset(20, 40));
    },
  );
  test(
    'cancel releases stylus suppression; second stylus cannot release owner',
    () {
      down(9, const Offset(80, 80), PointerDeviceKind.stylus);
      down(10, const Offset(90, 90), PointerDeviceKind.stylus);
      up(10, PointerDeviceKind.stylus);
      expect(view.stylusActive, isTrue);
      view.handle(
        const PointerCancelEvent(pointer: 9, kind: PointerDeviceKind.stylus),
        size,
      );
      expect(view.stylusActive, isFalse);
      down(1, const Offset(50, 50));
      move(1, const Offset(60, 60));
      expect(view.pan, const Offset(10, 10));
    },
  );
  test('active buffer appends without replacing its view; commit snapshots stay immutable', () {
    final ink = InkController('p', 'a');
    addTearDown(ink.dispose);
    void event(PointerEvent e) =>
        ink.handle(e, size, patientId: 'p', pageId: 'a', enabled: true);
    event(const PointerDownEvent(pointer: 1, kind: PointerDeviceKind.stylus));
    final buffer = ink.active.points;
    for (var i = 1; i <= 1000; i++) {
      event(
        PointerMoveEvent(
          pointer: 1,
          kind: PointerDeviceKind.stylus,
          position: Offset(i / 10, i / 10),
        ),
      );
    }
    expect(ink.active.points, same(buffer));
    expect(buffer.length, 1001);
    expect(() => buffer.clear(), throwsUnsupportedError);
    event(const PointerUpEvent(pointer: 1, kind: PointerDeviceKind.stylus));
    expect(ink.document.strokes.single.points.length, 1001);
    expect(buffer, isEmpty);
  });

  for (final language in ['en', 'ar']) {
    testWidgets(
      '$language navigation, zoomed ink alignment, suppression, reset and binding',
      (tester) async {
        Future<void> mount({String patient = 'a', String page = 'one'}) =>
            tester.pumpWidget(
              MaterialApp(
                locale: Locale(language),
                localizationsDelegates: AppLocalizations.localizationsDelegates,
                supportedLocales: AppLocalizations.supportedLocales,
                home: Scaffold(
                  body: Column(
                    children: [
                      const Text('Patient A', key: Key('patient-header')),
                      Expanded(
                        child: SingleChildScrollView(
                          child: SizedBox(
                            width: 420,
                            child: InkPage(
                              patientId: patient,
                              pageId: page,
                              enabled: true,
                              isCurrent: () => true,
                            ),
                          ),
                        ),
                      ),
                    ],
                  ),
                ),
              ),
            );
        await mount();
        final canvas = find.byKey(const Key('ink-input'));
        final origin = tester.getTopLeft(canvas);
        final header = tester.getTopLeft(
          find.byKey(const Key('patient-header')),
        );
        Matrix4 matrix() => tester
            .widget<Transform>(find.byKey(const Key('ink-transform')))
            .transform;
        InkPainter committed() =>
            tester
                    .widget<CustomPaint>(find.byKey(const Key('ink-committed')))
                    .painter!
                as InkPainter;
        final ctx = tester.element(find.byType(InkPage));
        expect(
          Directionality.of(ctx),
          language == 'ar' ? TextDirection.rtl : TextDirection.ltr,
        );
        final finger = await tester.startGesture(
          origin + const Offset(80, 80),
          pointer: 1,
        );
        await finger.moveTo(origin + const Offset(100, 100));
        await finger.up();
        await tester.pump();
        expect(matrix().getTranslation().x, closeTo(20, .01));
        expect(committed().strokes, isEmpty);
        final left = await tester.startGesture(
          origin + const Offset(100, 150),
          pointer: 2,
        );
        final right = await tester.startGesture(
          origin + const Offset(200, 150),
          pointer: 3,
        );
        await right.moveTo(origin + const Offset(300, 150));
        await tester.pump();
        await left.up();
        await right.up();
        expect(find.text('200%'), findsOneWidget);
        final transform = matrix().clone();
        final inkLocation = MatrixUtils.transformPoint(
          transform,
          const Offset(130, 120),
        );
        final pen = await tester.startGesture(
          origin + inkLocation,
          pointer: 4,
          kind: PointerDeviceKind.stylus,
        );
        await tester.pump();
        final activePainter = tester
            .widget<CustomPaint>(find.byKey(const Key('ink-active')))
            .painter;
        final oldCommitted = committed();
        final palm = await tester.startGesture(
          origin + const Offset(60, 60),
          pointer: 5,
        );
        await palm.moveTo(origin + const Offset(150, 180));
        final nextLocation = MatrixUtils.transformPoint(
          transform,
          const Offset(140, 130),
        );
        await pen.moveTo(origin + nextLocation);
        await tester.pump();
        expect(matrix(), transform);
        expect(committed(), same(oldCommitted));
        expect(
          tester
              .widget<CustomPaint>(find.byKey(const Key('ink-active')))
              .painter,
          same(activePainter),
        );
        final reset = tester.widget<TextButton>(
          find.byKey(const Key('ink-reset-view')),
        );
        expect(reset.onPressed, isNull);
        await pen.up();
        await palm.up();
        await tester.pump();
        final stroke = committed().strokes.single;
        expect(stroke.points.first.x, closeTo(65, .01));
        expect(stroke.points.first.y, closeTo(60, .01));
        expect(stroke.points.last.x, closeTo(70, .01));
        final resume = await tester.startGesture(
          origin + const Offset(100, 100),
          pointer: 6,
        );
        await resume.moveTo(origin + const Offset(120, 110));
        await resume.up();
        await tester.pump();
        expect(
          matrix().getTranslation().x,
          closeTo(transform.getTranslation().x + 20, .01),
        );
        expect(committed().strokes.single, same(stroke));
        expect(
          tester.getTopLeft(find.byKey(const Key('patient-header'))),
          header,
        );
        // Rendered paper uses exactly the same transform as input, in either directionality.
        final paperOrigin = tester.getTopLeft(
          find.byKey(const Key('ink-committed')),
        );
        expect(
          paperOrigin.dx,
          closeTo(origin.dx + matrix().getTranslation().x, .01),
        );
        await tester.tap(find.byKey(const Key('ink-reset-view')));
        await tester.pump();
        expect(find.text('100%'), findsOneWidget);
        expect(matrix(), Matrix4.identity());
        expect(committed().strokes.single, same(stroke));
        await mount(page: 'two');
        expect(committed().strokes, isEmpty);
        await mount(patient: 'b', page: 'two');
        expect(committed().strokes, isEmpty);
        await mount();
        expect(committed().strokes, isEmpty);
        expect(find.text('100%'), findsOneWidget);
        expect(tester.takeException(), isNull);
      },
    );
  }
}
