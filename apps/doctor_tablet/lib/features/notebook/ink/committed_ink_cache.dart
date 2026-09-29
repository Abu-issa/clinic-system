import 'dart:async';
import 'dart:math' as math;
import 'dart:ui' as ui;

import 'package:flutter/foundation.dart';

import 'ink_document.dart';

typedef InkRecorder = void Function(
  ui.Canvas canvas,
  ui.Size size,
  List<InkStroke> strokes,
);

/// Disposable derived display surface. Never serialized; vectors stay authoritative.
/// One image plus one in-flight replacement, each capped at eight million pixels.
/// Cached resolution covers up to 4x zoom where the budget permits. Above that
/// resolution, the painter must use the original vectors rather than blur ink.
class CommittedInkCache extends ChangeNotifier {
  CommittedInkCache(this.record);
  static const maxPixels = 8 * 1024 * 1024;
  // No patient data here: bound non-cancellable GPU work across fast page exits.
  static bool _rasterBusy = false;
  final InkRecorder record;
  List<InkStroke> _target = const [], _cached = const [];
  ui.Size _size = ui.Size.zero;
  double _ratio = 1;
  ui.Image? image;
  double maxZoom = 0;
  int get cachedCount => _cached.length;
  int get imageBytes => image == null ? 0 : image!.width * image!.height * 4;
  bool _busy = false, _disposed = false;
  Timer? _timer;

  static bool prefix(List<InkStroke> prefix, List<InkStroke> all) {
    if (prefix.length > all.length) return false;
    for (var i = 0; i < prefix.length; i++) {
      if (!identical(prefix[i], all[i])) return false;
    }
    return true;
  }

  void update(List<InkStroke> strokes, ui.Size size, double pixelRatio) {
    if (identical(strokes, _target) && size == _size && pixelRatio == _ratio) {
      return;
    }
    if (!prefix(_cached, strokes) || size != _size || pixelRatio != _ratio) {
      _clear();
    }
    _target = strokes;
    _size = size;
    _ratio = pixelRatio;
    _timer?.cancel();
    // Tiny pages are cheaper as vectors and don't need a texture allocation.
    if (strokes.fold<int>(0, (n, s) => n + s.points.length) < 2000) return;
    _schedule();
  }

  void _schedule() {
    _timer = Timer(const Duration(milliseconds: 200), () {
      unawaited(_build());
    });
  }

  Future<void> _build() async {
    if (_disposed || _busy || _size.isEmpty) return;
    // A queued refresh may outlive a clear/undo that made the page small.
    if (_target.fold<int>(0, (n, s) => n + s.points.length) < 2000) return;
    if (_rasterBusy) {
      _schedule();
      return;
    }
    _busy = true;
    final strokes = _target, size = _size, ratio = _ratio;
    final scale = math.min(
      ratio * 4,
      math.sqrt(maxPixels / (size.width * size.height)),
    );
    if (scale < ratio) {
      _busy = false;
      return;
    }
    _rasterBusy = true;
    ui.Picture? picture;
    try {
      final recorder = ui.PictureRecorder();
      final canvas = ui.Canvas(recorder)..scale(scale);
      try {
        record(canvas, size, strokes);
      } finally {
        picture = recorder.endRecording();
      }
      final result = await picture.toImage(
        (size.width * scale).floor(),
        (size.height * scale).floor(),
      );
      if (_disposed ||
          size != _size ||
          ratio != _ratio ||
          !prefix(strokes, _target)) {
        result.dispose();
      } else {
        _clear();
        image = result;
        _cached = strokes;
        maxZoom =
            math.min(result.width / size.width, result.height / size.height) /
            ratio;
        notifyListeners();
      }
    } catch (_) {
      // GPU allocation failure falls back to unchanged vector drawing.
    } finally {
      picture?.dispose();
      _busy = false;
      _rasterBusy = false;
      if (!_disposed &&
          (!identical(strokes, _target) || size != _size || ratio != _ratio)) {
        _schedule();
      }
    }
  }

  void _clear() {
    image?.dispose();
    image = null;
    _cached = const [];
    maxZoom = 0;
  }

  @override
  void dispose() {
    _disposed = true;
    _timer?.cancel();
    _clear();
    _target = const [];
    super.dispose();
  }
}
