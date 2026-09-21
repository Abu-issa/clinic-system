import 'dart:convert';
import 'dart:math';

import 'package:dio/dio.dart';

String notebookIdentifier() {
  final random = Random.secure();
  return List.generate(
    16,
    (_) => random.nextInt(256).toRadixString(16).padLeft(2, '0'),
  ).join();
}

final class NotebookPage {
  NotebookPage.fromJson(
    Map<String, dynamic> json,
    String patient, [
    String? page,
  ]) : id = json['id'] as String,
       patientId = json['patientId'] as String,
       title = json['title'] as String,
       created = DateTime.parse(json['createdAtUtc'] as String),
       updated = DateTime.parse(json['updatedAtUtc'] as String),
       finalized = json['finalizedAtUtc'] != null,
       revision = json['currentRevisionNumber'] as int,
       rowVersion = json['rowVersion'] as String {
    if (patientId != patient ||
        (page != null && id != page) ||
        id.isEmpty ||
        revision < 0 ||
        base64Decode(rowVersion).length != 8) {
      throw const FormatException('Invalid notebook projection');
    }
  }
  final String id, patientId, title, rowVersion;
  final DateTime created, updated;
  final bool finalized;
  final int revision;
}

/// Retained unchanged for an explicit retry after an ambiguous transport failure.
final class NotebookDraft {
  NotebookDraft(NotebookPage page, this.originDeviceId, this.amendment)
    : patientId = page.patientId,
      pageId = page.id,
      expectedRowVersion = page.rowVersion,
      clientDraftId = notebookIdentifier(),
      bytes = List.unmodifiable(
        utf8.encode(
          jsonEncode({
            'formatVersion': 1,
            'patientId': page.patientId,
            'pageId': page.id,
          }),
        ),
      );
  final String patientId,
      pageId,
      expectedRowVersion,
      clientDraftId,
      originDeviceId;
  final bool amendment;
  final List<int> bytes;
}

final class NotebookBatch {
  const NotebookBatch(this.items, this.hasMore);
  final List<NotebookPage> items;
  final bool hasMore;
}

final class NotebookApi {
  NotebookApi(this.dio);
  final Dio dio;
  String _path(String patient, [String? page]) =>
      '/api/staff/patients/${Uri.encodeComponent(patient)}/notebook/pages'
      '${page == null ? '' : '/${Uri.encodeComponent(page)}'}';

  Future<NotebookPage> detail(
    String patient,
    String page,
    CancelToken cancel,
  ) async {
    final response = await dio.get<Map<String, dynamic>>(
      _path(patient, page),
      cancelToken: cancel,
    );
    return NotebookPage.fromJson(response.data!, patient, page);
  }

  Future<NotebookBatch> list(
    String patient,
    int page,
    CancelToken cancel,
  ) async {
    final response = await dio.get<Map<String, dynamic>>(
      _path(patient),
      queryParameters: {'page': page, 'pageSize': 10},
      cancelToken: cancel,
    );
    final json = response.data!;
    final items = json['items'] as List;
    if (json['page'] != page ||
        json['pageSize'] != 10 ||
        items.length > 10 ||
        items.any((item) => item['patientId'] != patient)) {
      throw const FormatException('Invalid notebook list');
    }
    // The existing list projection omits finalization and concurrency metadata.
    final details = await Future.wait(
      items.map((item) => detail(patient, item['id'] as String, cancel)),
    );
    return NotebookBatch(List.unmodifiable(details), json['hasMore'] as bool);
  }

  Future<NotebookPage> create(
    String patient,
    String title,
    CancelToken cancel,
  ) async {
    final response = await dio.post<Map<String, dynamic>>(
      _path(patient),
      data: {'title': title},
      cancelToken: cancel,
    );
    return NotebookPage.fromJson(response.data!, patient);
  }

  Future<NotebookPage> finalize(NotebookPage page, CancelToken cancel) async {
    final response = await dio.post<Map<String, dynamic>>(
      '${_path(page.patientId, page.id)}/finalize',
      data: {'expectedRowVersion': page.rowVersion},
      cancelToken: cancel,
    );
    return NotebookPage.fromJson(response.data!, page.patientId, page.id);
  }

  Future<NotebookPage> submit(NotebookDraft draft, CancelToken cancel) async {
    await dio.post<Map<String, dynamic>>(
      '${_path(draft.patientId, draft.pageId)}/${draft.amendment ? 'amendments' : 'revisions'}',
      data: FormData.fromMap({
        'expectedRowVersion': draft.expectedRowVersion,
        'clientDraftId': draft.clientDraftId,
        'originDeviceId': draft.originDeviceId,
        'payload': MultipartFile.fromBytes(
          draft.bytes,
          filename: 'notebook.json',
          contentType: DioMediaType('application', 'json'),
        ),
      }),
      cancelToken: cancel,
    );
    // Replays can acknowledge an older revision. Read the current projection
    // instead of regressing local metadata to that older acknowledgement.
    return detail(draft.patientId, draft.pageId, cancel);
  }
}
