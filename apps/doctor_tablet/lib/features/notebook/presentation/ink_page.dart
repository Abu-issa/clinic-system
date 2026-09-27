import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';

import 'dart:math' as math;
import 'dart:async';

import '../../../l10n/app_localizations.dart';
import '../ink/ink_controller.dart';
import '../ink/ink_document.dart';
import '../ink/ink_viewport.dart';
import '../data/local_ink_draft.dart';
import '../state/local_drafts.dart';

class InkPage extends StatefulWidget {
  const InkPage({
    super.key,
    required this.patientId,
    required this.pageId,
    required this.enabled,
    required this.isCurrent,
    this.drafts,
    this.owner,
    this.serverRevision,
    this.serverRowVersion,
    this.selectedDraft,
  });
  final String patientId, pageId;
  final bool enabled;
  final LocalDrafts? drafts;
  final String? owner, serverRowVersion;
  final int? serverRevision;

  /// When supplied, navigation owns and releases this handle.
  final DraftHandle? selectedDraft;

  /// Rechecks the live patient/session/page at the input boundary.
  final bool Function() isCurrent;
  @override
  State<InkPage> createState() => _InkPageState();
}

class _InkPageState extends State<InkPage> {
  late InkController ink;
  DraftHandle? draft;
  bool get editable =>
      widget.enabled && (draft?.canEdit ?? widget.drafts == null);
  final view = InkViewport();
  Size? _lastSize;
  @override
  void initState() {
    super.initState();
    _attach();
  }

  void _changed() {
    if (mounted) setState(() {});
  }

  void _attach() {
    if (widget.selectedDraft != null ||
        (widget.drafts != null && widget.owner != null)) {
      draft =
          widget.selectedDraft ??
          widget.drafts!.open(
            DraftKey(widget.owner!, widget.patientId, widget.pageId),
            revision: widget.serverRevision,
            rowVersion: widget.serverRowVersion,
          );
      ink = draft!.ink;
      draft!.addListener(_changed);
    } else {
      ink = InkController(widget.patientId, widget.pageId);
    }
  }

  void _detach(LocalDrafts? manager, bool externallyOwned) {
    final handle = draft;
    if (handle != null) {
      handle.removeListener(_changed);
      if (!externallyOwned) manager!.leave(handle);
      draft = null;
    } else {
      ink.dispose();
    }
  }

  @override
  void didUpdateWidget(covariant InkPage oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.patientId != widget.patientId ||
        oldWidget.pageId != widget.pageId ||
        oldWidget.owner != widget.owner ||
        oldWidget.drafts != widget.drafts ||
        oldWidget.selectedDraft != widget.selectedDraft) {
      _detach(oldWidget.drafts, oldWidget.selectedDraft != null);
      _attach();
      view.cancelContacts();
      view.reset();
    }
    ink.bind(widget.patientId, widget.pageId);
    if (!editable || !widget.isCurrent()) {
      ink.finishActive();
      view.cancelContacts();
    }
  }

  @override
  void dispose() {
    _detach(widget.drafts, widget.selectedDraft != null);
    view.dispose();
    super.dispose();
  }

  void _tool(VoidCallback update) {
    if (!editable || !widget.isCurrent() || view.stylusActive) return;
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
    if (widget.drafts != null &&
        (draft == null || draft!.loading || draft!.failedRestore)) {
      return Column(
        children: [
          Text(draft?.loading == true ? s.inkLoading : s.inkRestoreFailed),
          if (draft?.failedRestore == true)
            TextButton(
              onPressed: () => unawaited(draft!.retryRestore()),
              child: Text(s.inkRetrySave),
            ),
        ],
      );
    }
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text(draft == null ? s.inkTemporary : s.inkLocalNotice),
        if (draft != null) ...[
          ListenableBuilder(
            listenable: ink.active,
            builder: (context, _) => Text(
              inkSaveLabel(s, draft!, ink.active.points.isNotEmpty),
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              key: const Key('ink-save-state'),
            ),
          ),
          // Async save results must not shift the page under an active pen.
          Visibility(
            visible: draft!.failed,
            maintainState: true,
            maintainAnimation: true,
            maintainSize: true,
            child: Text(s.inkSaveFailed),
          ),
          TextButton(
            onPressed: draft!.failed ? () => unawaited(draft!.flush()) : null,
            child: Text(s.inkRetrySave),
          ),
        ],
        ListenableBuilder(
          listenable: ink,
          builder: (context, _) => Wrap(
            spacing: 8,
            crossAxisAlignment: WrapCrossAlignment.center,
            children: [
              ChoiceChip(
                label: Text(s.inkPen),
                selected: ink.tool == InkTool.pen,
                onSelected: editable
                    ? (_) => _tool(() => ink.tool = InkTool.pen)
                    : null,
              ),
              ChoiceChip(
                label: Text(s.inkEraser),
                selected: ink.tool == InkTool.eraser,
                onSelected: editable
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
                  onSelected: editable
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
                  onSelected: editable
                      ? (_) => _tool(() => ink.color = entry.$1)
                      : null,
                ),
              IconButton(
                tooltip: s.inkUndo,
                icon: const Icon(Icons.undo),
                onPressed: editable && ink.canUndo
                    ? () => _tool(ink.undo)
                    : null,
              ),
              IconButton(
                tooltip: s.inkRedo,
                icon: const Icon(Icons.redo),
                onPressed: editable && ink.canRedo
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
                    enabled: editable && widget.isCurrent(),
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

String inkSaveLabel(AppLocalizations s, DraftHandle draft, bool active) {
  if (draft.syncing) return s.inkSyncing;
  if (draft.syncState == 'conflict') return s.inkConflict;
  if (draft.syncState == 'syncFailed') return s.inkSyncFailed;
  if (draft.dirty || !draft.saved || active) return s.inkLocalChanges;
  if (draft.syncState == 'offline') return s.inkOffline;
  if (draft.syncState == 'queued' || draft.queuedCount > 0) return s.inkQueued;
  if (draft.serverSynced) return s.inkServerSynced;
  return s.inkLocalSaved;
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
