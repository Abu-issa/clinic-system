/// Version 1 uses logical A4 coordinates, independent of display pixels.
final class InkDocument {
  InkDocument({
    required this.patientId,
    required this.pageId,
    Iterable<InkStroke> strokes = const [],
  }) : strokes = List.unmodifiable(strokes);
  static const int formatVersion = 1;
  static const double width = 210, height = 297;
  final String patientId, pageId;
  final List<InkStroke> strokes;
  bool matches(String patient, String page) =>
      patientId == patient && pageId == page;
}

final class InkStroke {
  InkStroke({
    required this.id,
    required this.color,
    required this.width,
    required Iterable<InkPoint> points,
  }) : points = List.unmodifiable(points);
  static const int formatVersion = 1;
  final int id, color;
  final double width;
  final List<InkPoint> points;

  // A constant reading (including the common synthetic 1.0) is not evidence
  // of pressure support. Any invalid sample makes this stroke fixed-width.
  bool get usesPressure {
    if (points.length < 2 || points.any((p) => p.pressure == null)) {
      return false;
    }
    final values = points.map((p) => p.pressure!);
    final low = values.reduce((a, b) => a < b ? a : b);
    final high = values.reduce((a, b) => a > b ? a : b);
    return high - low > 0.02;
  }
}

final class InkPoint {
  const InkPoint({
    required this.x,
    required this.y,
    required this.timeMicros,
    this.pressure,
  });
  static const int formatVersion = 1;
  final double x, y;

  /// Raw pointer event timestamp in microseconds (monotonic device time).
  final int timeMicros;

  /// Normalized [0, 1], or null when the device sample is unusable.
  final double? pressure;

  static double? normalizePressure(double value, double min, double max) {
    if (!value.isFinite ||
        !min.isFinite ||
        !max.isFinite ||
        max <= min ||
        value < min ||
        value > max) {
      return null;
    }
    return ((value - min) / (max - min)).clamp(0.0, 1.0);
  }
}
