import 'dart:async';

import 'package:dio/dio.dart';
import 'package:doctor_tablet/app/doctor_tablet_app.dart';
import 'package:doctor_tablet/app/session/session_cubit.dart';
import 'package:doctor_tablet/features/patients/data/patient_search.dart';
import 'package:doctor_tablet/features/patients/state/patient_context_cubit.dart';
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';

import 'auth_fixture.dart';

Map<String, dynamic> patient(String id, {int allergy = 0}) => {
  'patientId': id,
  'fullName': 'Patient $id',
  'medicalRecordNumber': 'MRN-$id',
  'dateOfBirth': '1990-02-03',
  'allergyStatus': allergy,
  'unmappedPrivateData': 'do not map',
};
ResponseBody page(
  FakeClinic backend,
  RequestOptions request,
  List<Map<String, dynamic>> items, {
  bool more = false,
}) => backend.reply(200, {
  'items': items,
  'page': (request.data as Map)['page'],
  'pageSize': 10,
  'hasMore': more,
});

void main() {
  testWidgets(
    'header survives refresh and expiry dismisses a patient switch dialog',
    (tester) async {
      final f = AuthFixture();
      f.backend.patientSearch = (r) async =>
          page(f.backend, r, [patient('one'), patient('two')]);
      final signIn = f.signIn();
      await tester.pumpAndSettle();
      await signIn;
      await tester.pumpWidget(DoctorTabletApp(sessionCubit: f.cubit));
      await tester.pumpAndSettle();
      final cubit = tester
          .element(find.byKey(const Key('patient-search-term')))
          .read<PatientContextCubit>();
      final search = cubit.search('Patient');
      await tester.pumpAndSettle();
      await search;
      cubit.select(cubit.state.items.first);
      await tester.pumpAndSettle();
      expect(find.text('حالة الحساسية غير معروفة'), findsOneWidget);
      f.backend.expiredAccess = true;
      f.backend.refreshGate = Completer<void>();
      final refreshSearch = cubit.search('Patient');
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 100));
      expect(f.cubit.state, isA<SessionRefreshing>());
      expect(find.byKey(const Key('patient-header')), findsOneWidget);
      f.backend.refreshGate!.complete();
      await tester.pumpAndSettle();
      await refreshSearch;
      await tester.ensureVisible(find.byKey(const Key('select-two')));
      await tester.tap(find.byKey(const Key('select-two')));
      await tester.pumpAndSettle();
      expect(find.byType(AlertDialog), findsOneWidget);
      f.backend.expiredAccess = true;
      f.backend.rejectRefresh = true;
      final expired = cubit.search('Patient');
      await tester.pumpAndSettle();
      await expired;
      expect(find.byType(AlertDialog), findsNothing);
      expect(find.byKey(const Key('patient-header')), findsNothing);
      expect(find.byKey(const Key('login')), findsOneWidget);
      expect(cubit.state.active, isNull);
    },
  );

  group('patient context state and API', () {
    late AuthFixture f;
    late PatientContextCubit patients;
    setUp(() async {
      f = AuthFixture();
      await f.signIn();
      patients = PatientContextCubit(f.cubit, PatientSearch(f.api));
      f.backend.patientSearch = (r) async =>
          page(f.backend, r, [patient('one', allergy: 2), patient('two')]);
    });
    tearDown(() async {
      await patients.close();
      await f.cubit.close();
    });

    test(
      'search success uses bounded POST body and selection is explicit',
      () async {
        f.backend.patientSearch = (r) async {
          expect(r.method, 'POST');
          expect(r.queryParameters, isEmpty);
          expect(r.data, {'searchTerm': 'MRN', 'page': 1, 'pageSize': 10});
          expect(r.headers['Authorization'], 'Bearer ${f.backend.access}');
          return page(f.backend, r, [patient('one', allergy: 2)]);
        };
        await patients.search(' MRN ');
        expect(patients.state.items, hasLength(1));
        expect(patients.state.active, isNull);
        patients.select(patients.state.items.single);
        expect(patients.state.active!.mrn, 'MRN-one');
        expect(
          patients.state.active!.allergyStatus,
          PatientAllergyStatus.hasKnownAllergies,
        );
      },
    );

    test('empty search results and invalid input', () async {
      var calls = 0;
      f.backend.patientSearch = (r) async {
        calls++;
        return page(f.backend, r, []);
      };
      await patients.search('');
      expect(patients.state.issue, PatientSearchIssue.invalidTerm);
      expect(calls, 0);
      await patients.search('nobody');
      expect(patients.state.searched, isTrue);
      expect(patients.state.items, isEmpty);
      expect(patients.state.hasMore, isFalse);
    });

    test(
      'load more appends once; duplicate calls and exhausted pages are bounded',
      () async {
        final pages = <int>[];
        f.backend.patientSearch = (r) async {
          final number = (r.data as Map)['page'] as int;
          pages.add(number);
          return page(f.backend, r, [
            patient(number.toString()),
          ], more: number == 1);
        };
        await patients.search('Patient');
        await Future.wait([patients.loadMore(), patients.loadMore()]);
        await patients.loadMore();
        expect(pages, [1, 2]);
        expect(patients.state.items.map((p) => p.patientId), ['1', '2']);
        await patients.search('Other');
        expect(patients.state.items, hasLength(1));
      },
    );

    test('selection remains until explicitly replaced', () async {
      await patients.search('Patient');
      patients.select(patients.state.items.first);
      final original = patients.state.active;
      await patients.search('Patient');
      expect(patients.state.active, same(original));
      patients.select(patients.state.items.last);
      expect(patients.state.active!.patientId, 'two');
    });

    test(
      '403 clears active patient and results; network error is safe',
      () async {
        await patients.search('Patient');
        patients.select(patients.state.items.first);
        f.backend.patientSearch = (r) async =>
            f.backend.reply(503, {'detail': 'private server details'});
        await patients.search('Patient');
        expect(patients.state.issue, PatientSearchIssue.network);
        expect(patients.state.active, isNotNull);
        f.backend.patientSearch = (r) async => f.backend.reply(403, {});
        await patients.search('Patient');
        expect(patients.state.issue, PatientSearchIssue.forbidden);
        expect(patients.state.active, isNull);
        expect(patients.state.items, isEmpty);
      },
    );

    test(
      'logout clears context and same staff logging in starts empty',
      () async {
        await patients.search('Patient');
        patients.select(patients.state.items.first);
        await f.cubit.logout();
        expect(patients.state.active, isNull);
        expect(patients.state.items, isEmpty);
        await f.cubit.login('staff', 'test-password');
        await f.cubit.verify('123456');
        expect(patients.state.active, isNull);
        expect(patients.state.term, isEmpty);
      },
    );

    test('refresh retains context but session expiry clears it', () async {
      await patients.search('Patient');
      patients.select(patients.state.items.first);
      f.backend.expiredAccess = true;
      await patients.search('Patient');
      expect(f.backend.refreshes, 1);
      expect(patients.state.active!.patientId, 'one');
      f.backend.expiredAccess = true;
      f.backend.rejectRefresh = true;
      await patients.search('Patient');
      expect(f.cubit.state, isA<SessionExpired>());
      expect(patients.state.active, isNull);
      expect(patients.state.items, isEmpty);
    });

    test('late response after logout cannot restore patient data', () async {
      final entered = Completer<void>();
      final gate = Completer<void>();
      f.backend.patientSearch = (r) async {
        entered.complete();
        await gate.future;
        return page(f.backend, r, [patient('late')]);
      };
      final search = patients.search('Patient');
      await entered.future;
      await f.cubit.logout();
      gate.complete();
      await search;
      expect(patients.state.active, isNull);
      expect(patients.state.items, isEmpty);
    });

    test('superseded query cannot overwrite newer results', () async {
      final entered = Completer<void>();
      final gate = Completer<void>();
      f.backend.patientSearch = (r) async {
        if ((r.data as Map)['searchTerm'] == 'old') {
          entered.complete();
          await gate.future;
        }
        return page(f.backend, r, [
          patient((r.data as Map)['searchTerm'] as String),
        ]);
      };
      final old = patients.search('old');
      await entered.future;
      await patients.search('new');
      gate.complete();
      await old;
      expect(patients.state.items.single.patientId, 'new');
    });
  });

  for (final language in ['ar', 'en']) {
    testWidgets(
      '$language patient search selection persistent header and confirmed switch',
      (tester) async {
        final f = AuthFixture();
        f.backend.patientSearch = (r) async => page(f.backend, r, [
          patient('one', allergy: 2),
          patient('two', allergy: 1),
        ]);
        final signIn = f.signIn();
        await tester.pumpAndSettle();
        await signIn;
        await tester.pumpWidget(
          DoctorTabletApp(
            sessionCubit: f.cubit,
            initialLocale: Locale(language),
          ),
        );
        await tester.pumpAndSettle();
        final cubit = tester
            .element(find.byKey(const Key('patient-search-term')))
            .read<PatientContextCubit>();
        expect(
          Directionality.of(
            tester.element(find.byKey(const Key('patient-search-term'))),
          ),
          language == 'ar' ? TextDirection.rtl : TextDirection.ltr,
        );
        await tester.enterText(
          find.byKey(const Key('patient-search-term')),
          'Patient',
        );
        await tester.tap(find.byKey(const Key('patient-search-submit')));
        await tester.pumpAndSettle();
        await tester.tap(find.byKey(const Key('select-one')));
        await tester.pumpAndSettle();
        expect(find.byKey(const Key('patient-header')), findsOneWidget);
        expect(
          tester
              .widget<Text>(find.byKey(const Key('active-patient-name')))
              .data,
          'Patient one',
        );
        expect(
          find.text(
            language == 'ar' ? 'توجد حساسية مسجلة' : 'Known allergies recorded',
          ),
          findsOneWidget,
        );
        await tester.ensureVisible(find.byKey(const Key('select-two')));
        await tester.tap(find.byKey(const Key('select-two')));
        await tester.pumpAndSettle();
        expect(cubit.state.active!.patientId, 'one');
        await tester.tap(find.byKey(const Key('confirm-patient-switch')));
        await tester.pumpAndSettle();
        expect(
          tester
              .widget<Text>(find.byKey(const Key('active-patient-name')))
              .data,
          'Patient two',
        );
        expect(
          find.text(
            language == 'ar' ? 'لا توجد حساسية معروفة' : 'No known allergies',
          ),
          findsOneWidget,
        );
        expect(
          tester.getTopLeft(find.byKey(const Key('patient-header'))).dy,
          lessThan(120),
        );
        final logout = f.cubit.logout();
        await tester.pumpAndSettle();
        await logout;
        expect(cubit.state.active, isNull);
        expect(find.byKey(const Key('patient-header')), findsNothing);
      },
    );
  }
}
