import 'dart:convert';
import 'dart:math';
import 'dart:typed_data';

import 'package:dio/dio.dart';

import 'server_ink_codec.dart';
import '../ink/ink_document.dart';

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
  NotebookDraft({
    required this.patientId,
    required this.pageId,
    required this.expectedRowVersion,
    required this.originDeviceId,
    required this.amendment,
    required List<int> payload,
  }) : bytes = List.unmodifiable(payload),
       clientDraftId = notebookIdentifier(),
       boundaryName = 'clinic-${notebookIdentifier()}';
  NotebookDraft.fromMap(Map<String, dynamic> map)
    : patientId = map['patientId'] as String,
      pageId = map['pageId'] as String,
      expectedRowVersion = map['expectedRowVersion'] as String,
      clientDraftId = map['clientDraftId'] as String,
      originDeviceId = map['originDeviceId'] as String,
      boundaryName = map['boundaryName'] as String,
      amendment = map['amendment'] as bool,
      bytes = List.unmodifiable(base64Decode(map['bytes'] as String)) {
    decodeServerInk(Uint8List.fromList(bytes), patientId, pageId);
    if (base64Decode(expectedRowVersion).length != 8 ||
        !RegExp(r'^[a-f0-9]{32}$').hasMatch(clientDraftId) ||
        !RegExp(r'^[a-f0-9]{32}$').hasMatch(originDeviceId) ||
        !RegExp(r'^clinic-[a-f0-9]{32}$').hasMatch(boundaryName)) {
      throw const FormatException('Invalid retry envelope');
    }
  }
  Map<String, dynamic> toMap() => {
    'patientId': patientId,
    'pageId': pageId,
    'expectedRowVersion': expectedRowVersion,
    'clientDraftId': clientDraftId,
    'originDeviceId': originDeviceId,
    'boundaryName': boundaryName,
    'amendment': amendment,
    'bytes': base64Encode(bytes),
  };
  final String patientId,
      pageId,
      expectedRowVersion,
      clientDraftId,
      originDeviceId;
  final String boundaryName;
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

  /// Only used after explicit destructive-discard confirmation. Read the
  /// immutable revision and verify that current metadata has not moved.
  Future<InkDocument> readCurrentInk(
    NotebookPage page,
    CancelToken cancel,
  ) async {
    if (page.revision == 0) {
      return InkDocument(patientId: page.patientId, pageId: page.id);
    }
    final response = await dio.get<ResponseBody>(
      '${_path(page.patientId, page.id)}/revisions/${page.revision}/payload',
      options: Options(responseType: ResponseType.stream),
      cancelToken: cancel,
    );
    final bytes = BytesBuilder(copy: false);
    await for (final chunk in response.data!.stream) {
      if (bytes.length + chunk.length > maxInkBytes) {
        throw const FormatException('Ink size limit');
      }
      bytes.add(chunk);
    }
    final payload = bytes.takeBytes();
    InkDocument document;
    if (payload.isNotEmpty &&
        payload.first == 0x7b &&
        payload.length <= 16384) {
      final legacy = jsonDecode(utf8.decode(payload));
      if (legacy is! Map ||
          legacy.length != 3 ||
          legacy['formatVersion'] != 1 ||
          legacy['patientId'] != page.patientId ||
          legacy['pageId'] != page.id) {
        throw const FormatException('Legacy ink binding mismatch');
      }
      document = InkDocument(patientId: page.patientId, pageId: page.id);
    } else {
      document = decodeServerInk(payload, page.patientId, page.id);
    }
    final latest = await detail(page.patientId, page.id, cancel);
    if (latest.rowVersion != page.rowVersion ||
        latest.revision != page.revision) {
      throw const FormatException('Server page changed during read');
    }
    return document;
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
    final response = await dio.post<Map<String, dynamic>>(
      '${_path(draft.patientId, draft.pageId)}/${draft.amendment ? 'amendments' : 'revisions'}',
      data: FormData.fromMap(
        {
          'expectedRowVersion': draft.expectedRowVersion,
          'clientDraftId': draft.clientDraftId,
          'originDeviceId': draft.originDeviceId,
          'payload': MultipartFile.fromBytes(
            draft.bytes,
            filename: 'notebook.msgpack',
            contentType: DioMediaType('application', 'msgpack'),
          ),
        },
        ListFormat.multi,
        false,
        draft.boundaryName,
      ),
      cancelToken: cancel,
    );
    // Replays can acknowledge an older revision. Read the current projection
    // instead of regressing local metadata to that older acknowledgement.
    final ack = response.data!;
    if (ack['revisionId'] is! String ||
        ack['revisionNumber'] is! int ||
        (ack['revisionNumber'] as int) < 1 ||
        ack['rowVersion'] is! String ||
        base64Decode(ack['rowVersion'] as String).length != 8) {
      throw const FormatException('Invalid revision acknowledgement');
    }
    final page = await detail(draft.patientId, draft.pageId, cancel);
    if (page.revision != ack['revisionNumber'] ||
        page.rowVersion != ack['rowVersion']) {
      throw DioException(
        requestOptions: response.requestOptions,
        response: Response(
          requestOptions: response.requestOptions,
          statusCode: 409,
          data: {'code': 'page_changed'},
        ),
      );
    }
    return page;
  }
}
