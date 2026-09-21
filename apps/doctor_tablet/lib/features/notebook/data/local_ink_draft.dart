import 'dart:convert';
import '../ink/ink_document.dart';

/// Local-only format. This is not a server transport contract.
final class DraftKey {
  const DraftKey(this.owner, this.patientId, this.pageId);
  final String owner, patientId, pageId;
  @override
  bool operator ==(Object other) => other is DraftKey && owner == other.owner &&
      patientId == other.patientId && pageId == other.pageId;
  @override
  int get hashCode => Object.hash(owner, patientId, pageId);
}

final class LocalInkDraft {
  LocalInkDraft({required this.key, required this.document, required this.updatedAt,
    this.serverRevision, this.serverRowVersion}) {
    if (key.owner.isEmpty || key.patientId.isEmpty || key.pageId.isEmpty ||
        !document.matches(key.patientId, key.pageId)) {
      throw const FormatException('Draft binding mismatch');
    }
  }
  final DraftKey key;
  final InkDocument document;
  final DateTime updatedAt;
  final int? serverRevision;
  final String? serverRowVersion;
  static const syncState = 'localOnly';
}

Map<String, Object?> encodeLocalDraft(LocalInkDraft draft) => {
  'owner': draft.key.owner, 'patientId': draft.key.patientId, 'pageId': draft.key.pageId,
  'formatVersion': InkDocument.formatVersion,
  'payload': jsonEncode({
    'formatVersion': InkDocument.formatVersion,
    'patientId': draft.document.patientId, 'pageId': draft.document.pageId,
    'strokes': [for (final s in draft.document.strokes) {
      'formatVersion': InkStroke.formatVersion, 'id': s.id, 'color': s.color, 'width': s.width,
      'points': [for (final p in s.points) {
        'formatVersion': InkPoint.formatVersion, 'x': p.x, 'y': p.y,
        'pressure': p.pressure, 'timeMicros': p.timeMicros,
      }],
    }],
  }),
  'serverRevision': draft.serverRevision, 'serverRowVersion': draft.serverRowVersion,
  'updatedAt': draft.updatedAt.toUtc().toIso8601String(), 'syncState': LocalInkDraft.syncState,
};

LocalInkDraft decodeLocalDraft((DraftKey, Map<String, Object?>) input) {
  final (key, row) = input;
  if (row['owner'] != key.owner || row['patientId'] != key.patientId ||
      row['pageId'] != key.pageId || row['formatVersion'] != InkDocument.formatVersion ||
      row['syncState'] != LocalInkDraft.syncState) {
    throw const FormatException('Draft binding or version mismatch');
  }
  final data = jsonDecode(row['payload'] as String) as Map<String, dynamic>;
  if (data['patientId'] != key.patientId || data['pageId'] != key.pageId ||
      data['formatVersion'] != InkDocument.formatVersion) {
    throw const FormatException('Ink binding or version mismatch');
  }
  double number(Object? value, double min, double max) {
    final n = (value as num).toDouble();
    if (!n.isFinite || n < min || n > max) throw const FormatException('Invalid ink sample');
    return n;
  }
  final ids = <int>{};
  final strokes = <InkStroke>[];
  for (final s in data['strokes'] as List) {
    final id = s['id'] as int;
    final color = s['color'] as int;
    if (s['formatVersion'] != InkStroke.formatVersion || id < 0 || !ids.add(id) || color < 0 || color > 0xffffffff) {
      throw const FormatException('Invalid stroke');
    }
    final points = <InkPoint>[];
    for (final p in s['points'] as List) {
      final time = p['timeMicros'] as int;
      if (p['formatVersion'] != InkPoint.formatVersion || time < 0) throw const FormatException('Invalid point');
      points.add(InkPoint(x: number(p['x'], 0, InkDocument.width),
        y: number(p['y'], 0, InkDocument.height), timeMicros: time,
        pressure: p['pressure'] == null ? null : number(p['pressure'], 0, 1)));
    }
    if (points.isEmpty) throw const FormatException('Empty stroke');
    strokes.add(InkStroke(id: id, color: color, width: number(s['width'], 0.01, 10), points: points));
  }
  final revision = row['serverRevision'] as int?;
  if (revision != null && revision < 0) throw const FormatException('Invalid revision');
  return LocalInkDraft(key: key,
    document: InkDocument(patientId: key.patientId, pageId: key.pageId, strokes: strokes),
    updatedAt: DateTime.parse(row['updatedAt'] as String),
    serverRevision: revision, serverRowVersion: row['serverRowVersion'] as String?);
}
