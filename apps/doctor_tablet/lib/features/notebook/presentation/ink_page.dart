import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';

import 'dart:math' as math;

import '../../../l10n/app_localizations.dart';
import '../ink/ink_controller.dart';
import '../ink/ink_document.dart';
import '../ink/ink_viewport.dart';

class InkPage extends StatefulWidget {
  const InkPage({
    super.key,
    required this.patientId,
    required this.pageId,
    required this.enabled,
    required this.isCurrent,
  });
  final String patientId, pageId;
  final bool enabled;

  /// Rechecks the live patient/session/page at the input boundary.
  final bool Function() isCurrent;
  @override
  State<InkPage> createState() => _InkPageState();
}

class _InkPageState extends State<InkPage> {
  late final InkController ink;
  final view = InkViewport();
  Size? _lastSize;
  @override
  void initState() {
    super.initState();
    ink = InkController(widget.patientId, widget.pageId);
  }

  @override
  void didUpdateWidget(covariant InkPage oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.patientId != widget.patientId ||
        oldWidget.pageId != widget.pageId) {
      view.cancelContacts();
      view.reset();
    }
    ink.bind(widget.patientId, widget.pageId);
    if (!widget.enabled || !widget.isCurrent()) {
      ink.cancel();
      view.cancelContacts();
    }
  }

  @override
  void dispose() {
    ink.dispose();
    view.dispose();
    super.dispose();
  }

  void _tool(VoidCallback update) {
    if (!widget.enabled || !widget.isCurrent() || view.stylusActive) return;
    ink.cancel();
    setState(update);
  }

  @override
  Widget build(BuildContext context) {
    if (!widget.isCurrent() ||
        !ink.document.matches(widget.patientId, widget.pageId)) {
      return const SizedBox.shrink();
    }
    final s = AppLocalizations.of(context);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text(s.inkTemporary),
        ListenableBuilder(
          listenable: ink,
          builder: (context, _) => Wrap(
            spacing: 8,
            crossAxisAlignment: WrapCrossAlignment.center,
            children: [
              ChoiceChip(
                label: Text(s.inkPen),
                selected: ink.tool == InkTool.pen,
                onSelected: widget.enabled
                    ? (_) => _tool(() => ink.tool = InkTool.pen)
                    : null,
              ),
              ChoiceChip(
                label: Text(s.inkEraser),
                selected: ink.tool == InkTool.eraser,
                onSelected: widget.enabled
                    ? (_) => _tool(() => ink.tool = InkTool.eraser)
                    : null,
              ),
              for (final entry in [
                (0.35, s.inkFine),
                (0.7, s.inkMedium),
                (1.2, s.inkBold),
              ])
                ChoiceChip(
                  label: Text(entry.$2),
                  selected: ink.width == entry.$1,
                  onSelected: widget.enabled
                      ? (_) => _tool(() => ink.width = entry.$1)
                      : null,
                ),
              for (final entry in [
                (0xff000000, s.inkBlack),
                (0xff1565c0, s.inkBlue),
                (0xffc62828, s.inkRed),
              ])
                ChoiceChip(
                  label: Text(entry.$2),
                  avatar: Icon(Icons.circle, color: Color(entry.$1), size: 16),
                  selected: ink.color == entry.$1,
                  onSelected: widget.enabled
                      ? (_) => _tool(() => ink.color = entry.$1)
                      : null,
                ),
              IconButton(
                tooltip: s.inkUndo,
                icon: const Icon(Icons.undo),
                onPressed: widget.enabled && ink.canUndo
                    ? () => _tool(ink.undo)
                    : null,
              ),
              IconButton(
                tooltip: s.inkRedo,
                icon: const Icon(Icons.redo),
                onPressed: widget.enabled && ink.canRedo
                    ? () => _tool(ink.redo)
                    : null,
              ),
            ],
          ),
        ),
        ListenableBuilder(
          listenable: view,
          builder: (context, _) => Wrap(
            spacing: 12,
            crossAxisAlignment: WrapCrossAlignment.center,
            children: [
              Text('${(view.zoom * 100).round()}%', key: const Key('ink-zoom')),
              // Reserve the larger label's layout so pen-down cannot shift
              // the viewport and therefore its coordinate origin.
              IndexedStack(
                index: view.stylusActive ? 1 : 0,
                children: [Text(s.inkNavigation), Text(s.inkStylusActive)],
              ),
              TextButton(
                key: const Key('ink-reset-view'),
                onPressed: view.stylusActive
                    ? null
                    : () {
                        if (widget.isCurrent()) view.reset();
                      },
                child: Text(s.inkResetView),
              ),
            ],
          ),
        ),
        LayoutBuilder(
          builder: (context, outer) => SizedBox(
            height: math.min(
              600,
              outer.maxWidth * InkDocument.height / InkDocument.width,
            ),
            child: LayoutBuilder(
              builder: (context, constraints) {
                final size = constraints.biggest;
                if (_lastSize != null && _lastSize != size) {
                  // A resize invalidates contact baselines. Cancel before accepting
                  // more samples; reset is performed after this layout frame.
                  ink.cancel();
                  WidgetsBinding.instance.addPostFrameCallback((_) {
                    if (mounted) {
                      view.cancelContacts();
                      view.reset();
                    }
                  });
                }
                _lastSize = size;
                void input(PointerEvent event) {
                  if (!widget.isCurrent()) {
                    ink.cancel();
                    view.cancelContacts();
                    return;
                  }
                  view.handle(event, size);
                  ink.handle(
                    event,
                    size,
                    patientId: widget.patientId,
                    pageId: widget.pageId,
                    enabled: widget.enabled && widget.isCurrent(),
                    pagePosition: view.toPage(event.localPosition, size),
                  );
                }

                return Listener(
                  key: const Key('ink-input'),
                  behavior: HitTestBehavior.opaque,
                  onPointerDown: input,
                  onPointerMove: input,
                  onPointerUp: input,
                  onPointerCancel: input,
                  // Own the canvas gesture arena so the enclosing notebook list
                  // cannot scroll under a pen or navigating finger. Both kinds
                  // use raw events, with independent ink and viewport state.
                  child: RawGestureDetector(
                    gestures: {
                      EagerGestureRecognizer:
                          GestureRecognizerFactoryWithHandlers<
                            EagerGestureRecognizer
                          >(
                            () => EagerGestureRecognizer(
                              supportedDevices: {
                                PointerDeviceKind.stylus,
                                PointerDeviceKind.invertedStylus,
                                PointerDeviceKind.touch,
                              },
                            ),
                            (_) {},
                          ),
                    },
                    child: ClipRect(
                      child: ColoredBox(
                        color: const Color(0xffeeeeee),
                        child: ListenableBuilder(
                          listenable: view,
                          builder: (context, child) => Transform(
                            key: const Key('ink-transform'),
                            transform: Matrix4.identity()
                              ..translateByDouble(
                                view.pan.dx,
                                view.pan.dy,
                                0,
                                1,
                              )
                              ..scaleByDouble(view.zoom, view.zoom, 1, 1),
                            alignment: Alignment.topLeft,
                            child: child,
                          ),
                          child: Stack(
                            children: [
                              Positioned(
                                left: view.origin(size).dx,
                                top: view.origin(size).dy,
                                width: InkDocument.width * view.fit(size),
                                height: InkDocument.height * view.fit(size),
                                child: ClipRect(
                                  child: Stack(
                                    fit: StackFit.expand,
                                    children: [
                                      RepaintBoundary(
                                        child: ColoredBox(
                                          color: Colors.white,
                                          child: ListenableBuilder(
                                            listenable: ink,
                                            builder: (context, _) =>
                                                CustomPaint(
                                                  key: const Key(
                                                    'ink-committed',
                                                  ),
                                                  painter: InkPainter(
                                                    ink.document.strokes,
                                                  ),
                                                ),
                                          ),
                                        ),
                                      ),
                                      RepaintBoundary(
                                        child: CustomPaint(
                                          key: const Key('ink-active'),
                                          painter: ActiveInkPainter(ink.active),
                                        ),
                                      ),
                                    ],
                                  ),
                                ),
                              ),
                            ],
                          ),
                        ),
                      ),
                    ),
                  ),
                );
              },
            ),
          ),
        ),
      ],
    );
  }
}

class InkPainter extends CustomPainter {
  InkPainter(this.strokes);
  final List<InkStroke> strokes;
  @override
  void paint(Canvas canvas, Size size) {
    canvas.save();
    canvas.scale(
      size.width / InkDocument.width,
      size.height / InkDocument.height,
    );
    for (final stroke in strokes) {
      final pressure = stroke.usesPressure;
      final paint = Paint()
        ..color = Color(stroke.color)
        ..strokeCap = StrokeCap.round;
      for (var i = 0; i < stroke.points.length; i++) {
        final p = stroke.points[i];
        final previous = stroke.points[i == 0 ? 0 : i - 1];
        paint.strokeWidth =
            stroke.width *
            (pressure ? 0.5 + (p.pressure! + previous.pressure!) / 2 : 1);
        if (i == 0) {
          canvas.drawCircle(Offset(p.x, p.y), paint.strokeWidth / 2, paint);
        } else {
          canvas.drawLine(
            Offset(previous.x, previous.y),
            Offset(p.x, p.y),
            paint,
          );
        }
      }
    }
    canvas.restore();
  }

  @override
  bool shouldRepaint(covariant InkPainter oldDelegate) =>
      !identical(strokes, oldDelegate.strokes);
}

/// Direct repaint notification coalesces pointer samples into display frames;
/// there is no widget rebuild or immutable stroke copy per sample.
class ActiveInkPainter extends CustomPainter {
  ActiveInkPainter(this.active) : super(repaint: active);
  final ActiveInk active;
  @override
  void paint(Canvas canvas, Size size) {
    canvas.save();
    canvas.scale(
      size.width / InkDocument.width,
      size.height / InkDocument.height,
    );
    final paint = Paint()
      ..color = Color(active.color)
      ..strokeCap = StrokeCap.round;
    final points = active.points;
    for (var i = 0; i < points.length; i++) {
      final p = points[i], previous = points[i == 0 ? 0 : i - 1];
      paint.strokeWidth =
          active.width *
          (active.usesPressure
              ? 0.5 + (p.pressure! + previous.pressure!) / 2
              : 1);
      if (i == 0) {
        canvas.drawCircle(Offset(p.x, p.y), paint.strokeWidth / 2, paint);
      } else {
        canvas.drawLine(
          Offset(previous.x, previous.y),
          Offset(p.x, p.y),
          paint,
        );
      }
    }
    canvas.restore();
  }

  @override
  bool shouldRepaint(covariant ActiveInkPainter oldDelegate) =>
      active != oldDelegate.active;
}
