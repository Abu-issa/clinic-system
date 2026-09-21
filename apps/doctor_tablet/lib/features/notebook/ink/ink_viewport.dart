import 'dart:math' as math;

import 'package:flutter/gestures.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/painting.dart';

import 'ink_document.dart';

/// View state only: never reads or edits a stroke. No rotation or inertia.
class InkViewport extends ChangeNotifier {
  static const minZoom = 0.5, maxZoom = 4.0;
  double zoom = 1;
  Offset pan = Offset.zero;
  final _touches = <int, Offset>{};
  int? _stylus;
  bool get stylusActive => _stylus != null;
  double fit(Size size) => math.min(
    size.width / InkDocument.width,
    size.height / InkDocument.height,
  );
  Offset origin(Size size) => Offset(
    (size.width - InkDocument.width * fit(size)) / 2,
    (size.height - InkDocument.height * fit(size)) / 2,
  );
  Offset toPage(Offset position, Size size) =>
      ((position - pan) / zoom - origin(size)) / fit(size);
  Offset toScreen(Offset point, Size size) =>
      (point * fit(size) + origin(size)) * zoom + pan;

  void reset() {
    if (stylusActive) return;
    _touches.clear();
    zoom = 1;
    pan = Offset.zero;
    notifyListeners();
  }

  void cancelContacts() {
    _touches.clear();
    if (_stylus != null) {
      _stylus = null;
      notifyListeners();
    }
  }

  void handle(PointerEvent event, Size size) {
    if (event.kind == PointerDeviceKind.stylus) {
      if (event is PointerDownEvent && _stylus == null) {
        _stylus = event.pointer;
        _touches.clear();
        notifyListeners();
      } else if (event.pointer == _stylus &&
          (event is PointerUpEvent || event is PointerCancelEvent)) {
        _stylus = null;
        notifyListeners();
      }
      return;
    }
    if (event.kind != PointerDeviceKind.touch || stylusActive) return;
    final p = event.localPosition;
    if (!p.dx.isFinite || !p.dy.isFinite || size.isEmpty) return;
    if (event is PointerDownEvent) {
      if (_touches.length < 2) _touches[event.pointer] = p;
    } else if (event is PointerUpEvent || event is PointerCancelEvent) {
      _touches.remove(event.pointer);
    } else if (event is PointerMoveEvent &&
        _touches.containsKey(event.pointer)) {
      final before = _touches.values.toList();
      _touches[event.pointer] = p;
      final after = _touches.values.toList();
      final oldCenter =
          before.reduce((a, b) => a + b) / before.length.toDouble();
      final center = after.reduce((a, b) => a + b) / after.length.toDouble();
      var nextZoom = zoom;
      if (before.length == 2) {
        final distance = (before[0] - before[1]).distance;
        if (distance > 2) {
          nextZoom = (zoom * (after[0] - after[1]).distance / distance).clamp(
            minZoom,
            maxZoom,
          );
        }
      }
      pan = center - (oldCenter - pan) * (nextZoom / zoom);
      zoom = nextZoom;
      // Always retain a visible portion of the paper; no unbounded offsets.
      final start = origin(size) * zoom;
      final paper =
          Offset(InkDocument.width, InkDocument.height) * fit(size) * zoom;
      final marginX = math.min(32.0, math.min(size.width, paper.dx) / 4);
      final marginY = math.min(32.0, math.min(size.height, paper.dy) / 4);
      pan = Offset(
        pan.dx.clamp(
          marginX - start.dx - paper.dx,
          size.width - marginX - start.dx,
        ),
        pan.dy.clamp(
          marginY - start.dy - paper.dy,
          size.height - marginY - start.dy,
        ),
      );
      notifyListeners();
    }
  }
}
