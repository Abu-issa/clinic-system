import 'dart:math' as math;
import 'dart:collection';

import 'package:flutter/gestures.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/painting.dart';

import 'ink_document.dart';

enum InkTool { pen, eraser }

/// Mutable contact buffer; immutable model snapshots are made only on commit
/// (or explicit inspection), never copied on every pointer move.
final class ActiveInk extends ChangeNotifier
    implements ValueListenable<InkStroke?> {
  final _points = <InkPoint>[];
  late final List<InkPoint> points = UnmodifiableListView(_points);
  int id = 0, color = 0xff000000;
  double width = 0.7;
  double _min = 1, _max = 0;
  bool _validPressure = true;
  bool get usesPressure =>
      _validPressure && _points.length > 1 && _max - _min > 0.02;
  @override
  InkStroke? get value => _points.isEmpty
      ? null
      : InkStroke(id: id, color: color, width: width, points: _points);
  void start(int nextId, int nextColor, double nextWidth, InkPoint point) {
    id = nextId;
    color = nextColor;
    width = nextWidth;
    add(point);
  }

  void add(InkPoint point) {
    _points.add(point);
    final p = point.pressure;
    if (p == null) {
      _validPressure = false;
    } else {
      _min = math.min(_min, p);
      _max = math.max(_max, p);
    }
    notifyListeners();
  }

  void clear() {
    final changed = _points.isNotEmpty;
    _points.clear();
    _min = 1;
    _max = 0;
    _validPressure = true;
    if (changed) notifyListeners();
  }
}

/// Only the active overlay is notified on pen movement. Committed state changes
/// once per completed edit. History belongs exclusively to this binding.
final class InkController extends ChangeNotifier {
  InkController(String patientId, String pageId)
    : _document = InkDocument(patientId: patientId, pageId: pageId);
  InkDocument _document;
  InkDocument get document => _document;
  final active = ActiveInk();
  final _undo = <List<InkStroke>>[], _redo = <List<InkStroke>>[];
  int? _pointer;
  int _nextId = 0;
  InkTool tool = InkTool.pen;
  double width = 0.7;
  int color = 0xff000000;
  final _eraserPoints = <Offset>[];
  bool get canUndo => _undo.isNotEmpty;
  bool get canRedo => _redo.isNotEmpty;

  void restore(InkDocument document) {
    if (!_document.matches(document.patientId, document.pageId)) {
      throw const FormatException('Ink binding mismatch');
    }
    cancel();
    _undo.clear(); _redo.clear();
    _nextId = document.strokes.fold(0, (next, stroke) => math.max(next, stroke.id + 1));
    _document = document;
    notifyListeners();
  }

  /// Preserve a partial pen stroke before lifecycle/switch/logout boundaries.
  void finishActive() {
    final stroke = active.value;
    if (stroke != null) _commit([..._document.strokes, stroke]);
    cancel();
  }

  void bind(String patientId, String pageId) {
    if (_document.matches(patientId, pageId)) return;
    cancel();
    _undo.clear();
    _redo.clear();
    _document = InkDocument(patientId: patientId, pageId: pageId);
    notifyListeners();
  }

  void cancel() {
    _pointer = null;
    _eraserPoints.clear();
    active.clear();
  }

  void handle(
    PointerEvent event,
    Size size, {
    required String patientId,
    required String pageId,
    required bool enabled,
    Offset? pagePosition,
  }) {
    if (!_document.matches(patientId, pageId) || !enabled) {
      cancel();
      return;
    }
    // Touch, mouse and inverted-stylus buttons are deliberately unsupported.
    if (event.kind != PointerDeviceKind.stylus) return;
    if (event is PointerCancelEvent && event.pointer == _pointer) {
      cancel();
      return;
    }
    if (size.isEmpty || !size.width.isFinite || !size.height.isFinite) return;
    final position =
        pagePosition ??
        Offset(
          event.localPosition.dx / size.width * InkDocument.width,
          event.localPosition.dy / size.height * InkDocument.height,
        );
    if (!position.dx.isFinite || !position.dy.isFinite) return;
    final point = InkPoint(
      x: position.dx.clamp(0, InkDocument.width),
      y: position.dy.clamp(0, InkDocument.height),
      timeMicros: event.timeStamp.inMicroseconds,
      pressure: InkPoint.normalizePressure(
        event.pressure,
        event.pressureMin,
        event.pressureMax,
      ),
    );
    if (event is PointerDownEvent) {
      if (_pointer != null ||
          !(Offset.zero & const Size(InkDocument.width, InkDocument.height))
              .contains(position)) {
        return;
      }
      _pointer = event.pointer;
      if (tool == InkTool.pen) {
        active.start(_nextId++, color, width, point);
      } else {
        _eraserPoints.add(Offset(point.x, point.y));
      }
    } else if (event.pointer == _pointer) {
      if (event is PointerMoveEvent) {
        if (active.points.isNotEmpty) {
          active.add(point);
        } else {
          _eraserPoints.add(Offset(point.x, point.y));
        }
      } else if (event is PointerUpEvent) {
        final stroke = active.value;
        if (stroke != null) {
          // Up pressure commonly resets to zero: retain contact samples only.
          _commit([..._document.strokes, stroke]);
        } else {
          _eraserPoints.add(Offset(point.x, point.y));
          final remaining = _document.strokes.where((s) => !_hit(s)).toList();
          if (remaining.length != _document.strokes.length) _commit(remaining);
        }
        cancel();
      }
    }
  }

  bool _hit(InkStroke stroke) {
    final points = stroke.points.map((p) => Offset(p.x, p.y)).toList();
    for (var i = 0; i < _eraserPoints.length; i++) {
      final a = _eraserPoints[i], b = _eraserPoints[math.max(0, i - 1)];
      for (var j = 0; j < points.length; j++) {
        if (_segmentDistance(a, b, points[j], points[math.max(0, j - 1)]) <=
            2.5 + stroke.width * 0.75) {
          return true;
        }
      }
    }
    return false;
  }

  void _commit(List<InkStroke> strokes) {
    _undo.add(_document.strokes);
    if (_undo.length > 100) _undo.removeAt(0);
    _redo.clear();
    _replace(strokes);
  }

  void _replace(List<InkStroke> strokes) {
    _document = InkDocument(
      patientId: _document.patientId,
      pageId: _document.pageId,
      strokes: strokes,
    );
    notifyListeners();
  }

  void undo() {
    cancel();
    if (!canUndo) return;
    _redo.add(_document.strokes);
    _replace(_undo.removeLast());
  }

  void redo() {
    cancel();
    if (!canRedo) return;
    _undo.add(_document.strokes);
    _replace(_redo.removeLast());
  }

  @override
  void dispose() {
    active.dispose();
    super.dispose();
  }
}

double _pointDistance(Offset p, Offset a, Offset b) {
  final d = b - a;
  final t = d.distanceSquared == 0
      ? 0.0
      : (((p - a).dx * d.dx + (p - a).dy * d.dy) / d.distanceSquared).clamp(
          0.0,
          1.0,
        );
  return (p - (a + d * t)).distance;
}

double _cross(Offset a, Offset b) => a.dx * b.dy - a.dy * b.dx;
double _segmentDistance(Offset a, Offset b, Offset c, Offset d) {
  final denominator = _cross(b - a, d - c);
  if (denominator.abs() > 1e-10) {
    final t = _cross(c - a, d - c) / denominator;
    final u = _cross(c - a, b - a) / denominator;
    if (t >= 0 && t <= 1 && u >= 0 && u <= 1) return 0;
  }
  return [
    _pointDistance(a, c, d),
    _pointDistance(b, c, d),
    _pointDistance(c, a, b),
    _pointDistance(d, a, b),
  ].reduce(math.min);
}
