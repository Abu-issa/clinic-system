import 'dart:typed_data';

import 'package:msgpack_dart/msgpack_dart.dart' as pack;

import '../ink/ink_document.dart';

const serverInkVersion = 2,
    maxInkBytes = 4 * 1024 * 1024,
    maxInkStrokes = 4096,
    maxStrokePoints = 20000,
    maxInkPoints = 100000,
    maxInkTime = 86400000000,
    maxInkId = 9007199254740991;

/// Fixed map insertion order, numeric representation and stroke/point order.
/// Timing is microseconds relative to the first contact in each stroke.
Uint8List encodeServerInk(InkDocument document) {
  if (document.strokes.length > maxInkStrokes ||
      document.patientId.length > 128 ||
      document.pageId.length > 128) {
    throw const FormatException('Ink limit');
  }
  var count = 0;
  for (final s in document.strokes) {
    count += s.points.length;
    if (s.points.isEmpty ||
        s.points.length > maxStrokePoints ||
        count > maxInkPoints) {
      throw const FormatException('Point limit');
    }
  }
  final bytes = pack.serialize({
    'formatVersion': serverInkVersion,
    'patientId': document.patientId,
    'pageId': document.pageId,
    'pageWidthUnits': 210.0,
    'pageHeightUnits': 297.0,
    'strokes': [
      for (final s in document.strokes)
        {
          'id': s.id,
          'color': s.color,
          'width': s.width,
          'points': [
            for (final p in s.points)
              [p.x, p.y, p.pressure, p.timeMicros - s.points.first.timeMicros],
          ],
        },
    ],
  });
  decodeServerInk(bytes, document.patientId, document.pageId);
  return bytes;
}

InkDocument decodeServerInk(Uint8List bytes, String patientId, String pageId) {
  if (bytes.isEmpty || bytes.length > maxInkBytes) {
    throw const FormatException('Ink size limit');
  }
  _BoundedMessagePack(bytes).check();
  final data = pack.deserialize(bytes) as Map;
  if (data.length != 6 ||
      data['formatVersion'] != serverInkVersion ||
      data['patientId'] != patientId ||
      data['pageId'] != pageId ||
      data['pageWidthUnits'] != 210 ||
      data['pageHeightUnits'] != 297) {
    throw const FormatException('Ink binding, dimensions or version mismatch');
  }
  double number(Object? n, double low, double high) {
    if (n is! num || !n.isFinite || n < low || n > high) {
      throw const FormatException('Invalid ink number');
    }
    return n.toDouble();
  }

  final source = data['strokes'] as List;
  if (source.length > maxInkStrokes) {
    throw const FormatException('Stroke limit');
  }
  var total = 0;
  final ids = <int>{}, strokes = <InkStroke>[];
  for (final value in source) {
    final s = value as Map;
    final id = s['id'] as int, color = s['color'] as int;
    final points = s['points'] as List;
    total += points.length;
    if (s.length != 4 ||
        id < 0 ||
        id > maxInkId ||
        !ids.add(id) ||
        color < 0 ||
        color > 0xffffffff ||
        points.isEmpty ||
        points.length > maxStrokePoints ||
        total > maxInkPoints) {
      throw const FormatException('Invalid stroke or point count');
    }
    var previous = 0;
    final samples = <InkPoint>[];
    for (final value in points) {
      final p = value as List;
      if (p.length != 4 || p[3] is! int) {
        throw const FormatException('Invalid point');
      }
      final time = p[3] as int;
      if (time < previous ||
          time > maxInkTime ||
          (samples.isEmpty && time != 0)) {
        throw const FormatException('Invalid time');
      }
      samples.add(
        InkPoint(
          x: number(p[0], 0, 210),
          y: number(p[1], 0, 297),
          pressure: p[2] == null ? null : number(p[2], 0, 1),
          timeMicros: time,
        ),
      );
      previous = time;
    }
    strokes.add(
      InkStroke(
        id: id,
        color: color,
        width: number(s['width'], .01, 10),
        points: samples,
      ),
    );
  }
  return InkDocument(patientId: patientId, pageId: pageId, strokes: strokes);
}

/// Validate lengths/depth before msgpack_dart allocates containers. No arbitrary
/// extensions, binary values, compressed data, duplicate keys or trailing data.
class _BoundedMessagePack {
  _BoundedMessagePack(this.bytes) : data = ByteData.sublistView(bytes);
  final Uint8List bytes;
  final ByteData data;
  int offset = 0, nodes = 0;
  Never bad() => throw const FormatException('Malformed MessagePack');
  int read(int n) {
    if (offset + n > bytes.length) bad();
    final value = n == 1
        ? data.getUint8(offset)
        : n == 2
        ? data.getUint16(offset)
        : data.getUint32(offset);
    offset += n;
    return value;
  }

  void skip(int n) {
    if (offset + n > bytes.length) bad();
    offset += n;
  }

  void check() {
    item(0);
    if (offset != bytes.length) bad();
  }

  void item(int depth) {
    if (depth > 5 || ++nodes > 600000) bad();
    final code = read(1);
    if (code < 128 || code >= 224 || code == 0xc0) return;
    if (code >= 0xa0 && code <= 0xbf) {
      skip(code & 31);
      return;
    }
    if (code == 0xd9 || code == 0xda || code == 0xdb) {
      final length = read(
        code == 0xd9
            ? 1
            : code == 0xda
            ? 2
            : 4,
      );
      if (length > 128) bad();
      skip(length);
      return;
    }
    final numeric = {
      0xcc: 1,
      0xcd: 2,
      0xce: 4,
      0xcf: 8,
      0xd0: 1,
      0xd1: 2,
      0xd2: 4,
      0xd3: 8,
      0xca: 4,
      0xcb: 8,
    };
    if (numeric.containsKey(code)) {
      skip(numeric[code]!);
      return;
    }
    final map = (code >= 0x80 && code <= 0x8f) || code == 0xde || code == 0xdf;
    final array =
        (code >= 0x90 && code <= 0x9f) || code == 0xdc || code == 0xdd;
    if (!map && !array) bad();
    final count = code <= 0x9f
        ? code & 15
        : read(code == 0xdc || code == 0xde ? 2 : 4);
    if (count > (map ? 6 : maxStrokePoints)) bad();
    final keys = <String>{};
    for (var i = 0; i < count; i++) {
      if (map) {
        final start = offset;
        item(depth + 1);
        final key = pack.deserialize(
          Uint8List.sublistView(bytes, start, offset),
        );
        if (key is! String || !keys.add(key)) bad();
      }
      item(depth + 1);
    }
  }
}
