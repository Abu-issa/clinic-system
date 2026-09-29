import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';

import '../ink/ink_document.dart';
import 'server_ink_codec.dart';

final class NotebookRevision {
  NotebookRevision(Map<String, dynamic> json)
    : number = json['revisionNumber'] as int,
      kind = json['kind'] as String,
      author = json['authorStaffId'] as String,
      created = DateTime.parse(json['createdAtUtc'] as String),
      hasPayload = json['hasPayload'] as bool {
    if (number < 1 ||
        author.trim().isEmpty ||
        !['Created', 'Revision', 'Amendment'].contains(kind) ||
        hasPayload != (kind != 'Created')) {
      throw const FormatException('Invalid revision metadata');
    }
  }
  final int number;
  final String kind, author;
  final DateTime created;
  final bool hasPayload;
}

final class HistoricalInk {
  const HistoricalInk(this.document, {this.legacy = false});
  final InkDocument document;
  final bool legacy;
}

final class RevisionBatch {
  const RevisionBatch(this.items, this.hasMore, this.latest);
  final List<NotebookRevision> items;
  final bool hasMore;
  final int latest;
}

final class NotebookHistoryApi {
  NotebookHistoryApi(this.dio);
  final Dio dio;
  String path(String patient, String page) =>
      '/api/staff/patients/${Uri.encodeComponent(patient)}/notebook/pages/${Uri.encodeComponent(page)}/revisions';
  Future<RevisionBatch> list(
    String patient,
    String pageId,
    int page,
    CancelToken cancel,
  ) async {
    final response = await dio.get<Map<String, dynamic>>(
      path(patient, pageId),
      queryParameters: {'page': page, 'pageSize': 20},
      cancelToken: cancel,
    );
    final json = response.data!;
    if (json['patientId'] != patient ||
        json['pageId'] != pageId ||
        json['page'] != page ||
        json['pageSize'] != 20) {
      throw const FormatException('History binding mismatch');
    }
    final latest = json['currentRevisionNumber'] as int;
    final items = (json['items'] as List)
        .map((v) => NotebookRevision(v as Map<String, dynamic>))
        .toList();
    if (items.length > 20 ||
        latest < 1 ||
        items.any((r) => r.number > latest) ||
        [
          for (var i = 1; i < items.length; i++)
            items[i - 1].number <= items[i].number,
        ].contains(true)) {
      throw const FormatException('Invalid revision ordering');
    }
    return RevisionBatch(
      List.unmodifiable(items),
      json['hasMore'] as bool,
      latest,
    );
  }

  Future<HistoricalInk> read(
    String patient,
    String page,
    NotebookRevision revision,
    CancelToken cancel,
  ) async {
    if (!revision.hasPayload) {
      return HistoricalInk(InkDocument(patientId: patient, pageId: page));
    }
    final response = await dio.get<ResponseBody>(
      '${path(patient, page)}/${revision.number}/payload',
      options: Options(responseType: ResponseType.stream),
      cancelToken: cancel,
    );
    // The immutable private endpoint binds the revision number. v2 ink itself
    // binds patient/page, but deliberately contains no server revision number.
    if (response.headers.value('X-Notebook-Patient') != patient ||
        response.headers.value('X-Notebook-Page') != page ||
        response.headers.value('X-Notebook-Revision') != '${revision.number}') {
      await response.data!.stream.listen((_) {}).cancel();
      throw const FormatException('Historical response binding mismatch');
    }
    final bytes = BytesBuilder(copy: false);
    await for (final chunk in response.data!.stream) {
      if (bytes.length + chunk.length > maxInkBytes) {
        throw const FormatException('Ink size limit');
      }
      bytes.add(chunk);
    }
    final payload = bytes.takeBytes();
    // JSON whitespace is valid for the legacy metadata-only contract.
    final first = payload
        .where((b) => ![9, 10, 13, 32].contains(b))
        .firstOrNull;
    if (first == 123 && payload.length <= 16384) {
      final json = jsonDecode(utf8.decode(payload));
      if (json is! Map ||
          json.length != 3 ||
          json['formatVersion'] != 1 ||
          json['patientId'] != patient ||
          json['pageId'] != page) {
        throw const FormatException('Legacy binding mismatch');
      }
      return HistoricalInk(
        InkDocument(patientId: patient, pageId: page),
        legacy: true,
      );
    }
    return HistoricalInk(decodeServerInk(payload, patient, page));
  }
}
