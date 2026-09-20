import 'package:dio/dio.dart';

enum PatientAllergyStatus { unknown, noKnownAllergies, hasKnownAllergies }

/// Only the server's minimal patient-context projection is mapped.
final class PatientContext {
  PatientContext.fromJson(Map<String, dynamic> json)
    : patientId = json['patientId'] as String,
      fullName = json['fullName'] as String,
      mrn = json['medicalRecordNumber'] as String?,
      dateOfBirth = json['dateOfBirth'] == null
          ? null
          : DateTime.parse(json['dateOfBirth'] as String),
      allergyStatus = switch (json['allergyStatus']) {
        1 => PatientAllergyStatus.noKnownAllergies,
        2 => PatientAllergyStatus.hasKnownAllergies,
        _ => PatientAllergyStatus.unknown,
      } {
    if (patientId.isEmpty || fullName.isEmpty) {
      throw const FormatException('Invalid patient context.');
    }
  }
  final String patientId;
  final String fullName;
  final String? mrn;
  final DateTime? dateOfBirth;
  final PatientAllergyStatus allergyStatus;
  @override
  String toString() => 'PatientContext(redacted)';
}

final class PatientSearchPage {
  const PatientSearchPage(this.items, this.hasMore);
  final List<PatientContext> items;
  final bool hasMore;
}

final class PatientSearch {
  const PatientSearch(this.api);
  final Dio api;
  static const pageSize = 10;
  static const maxPage = 100;
  Future<PatientSearchPage> search(
    String term,
    int page,
    CancelToken cancel,
  ) async {
    final response = await api.post<Map<String, dynamic>>(
      '/api/mobile/staff/patients/search',
      data: {'searchTerm': term, 'page': page, 'pageSize': pageSize},
      cancelToken: cancel,
    );
    final json = response.data!;
    final items = (json['items'] as List)
        .map((item) => PatientContext.fromJson(item as Map<String, dynamic>))
        .toList();
    if (json['page'] != page ||
        json['pageSize'] != pageSize ||
        items.length > pageSize) {
      throw const FormatException('Invalid patient search page.');
    }
    return PatientSearchPage(List.unmodifiable(items), json['hasMore'] as bool);
  }
}
