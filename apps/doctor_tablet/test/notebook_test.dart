import 'dart:async';
import 'dart:convert';

import 'package:dio/dio.dart';
import 'package:doctor_tablet/app/doctor_tablet_app.dart';
import 'package:doctor_tablet/features/notebook/data/notebook_api.dart';
import 'package:doctor_tablet/features/notebook/state/notebook_cubit.dart';
import 'package:doctor_tablet/features/patients/data/patient_search.dart';
import 'package:doctor_tablet/features/patients/state/patient_context_cubit.dart';
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';

import 'auth_fixture.dart';
import 'patient_context_test.dart' show patient, page;

Map<String, dynamic> projection(
  String patientId,
  String id, {
  int revision = 0,
  bool finalized = false,
}) => {
  'id': id,
  'patientId': patientId,
  'title': 'Page $id',
  'createdAtUtc': '2026-09-21T10:00:00Z',
  'updatedAtUtc': '2026-09-21T11:00:00Z',
  'finalizedAtUtc': finalized ? '2026-09-21T11:00:00Z' : null,
  'currentRevisionNumber': revision,
  'rowVersion': base64Encode([0, 0, 0, 0, 0, 0, finalized ? 1 : 0, revision]),
};

final class NotebookServer {
  NotebookServer(this.f) {
    f.backend.notebook = handle;
  }
  final AuthFixture f;
  final pages = <String, Map<String, dynamic>>{'a': projection('one', 'a')};
  final requests = <RequestOptions>[];
  final drafts = <String>{};
  bool more = false,
      empty = false,
      fail = false,
      stale = false,
      loseReply = false;
  int mutations = 0;
  Completer<void>? gate;
  Future<ResponseBody> handle(RequestOptions r) async {
    requests.add(r);
    await gate?.future;
    if (fail) return f.backend.reply(503, {});
    final parts = r.path.split('/');
    final patientId = parts[4];
    final tail = parts.last;
    if (tail == 'pages') {
      if (r.method == 'POST') {
        expectSync(r.data, {'title': 'New page'});
        pages['new'] = projection(patientId, 'new')..['title'] = 'New page';
        return f.backend.reply(201, pages['new']!);
      }
      final number = r.queryParameters['page'];
      return f.backend.reply(200, {
        'items': empty
            ? []
            : [
                if (number == 1)
                  ...pages.values
                      .where((p) => p['patientId'] == patientId)
                      .take(1)
                else
                  projection(patientId, 'b'),
              ],
        'page': number,
        'pageSize': 10,
        'hasMore': more && number == 1,
      });
    }
    if (r.method == 'GET') {
      return f.backend.reply(200, pages[tail] ?? projection(patientId, tail));
    }
    if (stale) return f.backend.reply(409, {'code': 'page_changed'});
    final id = parts[parts.length - 2];
    final current = pages[id]!;
    if (tail == 'finalize') {
      expectSync(r.data, {'expectedRowVersion': current['rowVersion']});
      pages[id] = projection(
        patientId,
        id,
        revision: current['currentRevisionNumber'] as int,
        finalized: true,
      );
      return f.backend.reply(200, pages[id]!);
    }
    final form = r.data as FormData;
    final fields = Map.fromEntries(form.fields);
    expectSync(fields.keys.toSet(), {
      'expectedRowVersion',
      'clientDraftId',
      'originDeviceId',
    });
    expectSync(form.files.single.key, 'payload');
    if (drafts.add(fields['clientDraftId']!)) {
      expectSync(fields['expectedRowVersion'], current['rowVersion']);
      mutations++;
      pages[id] = projection(
        patientId,
        id,
        revision: (current['currentRevisionNumber'] as int) + 1,
        finalized: current['finalizedAtUtc'] != null,
      );
    }
    if (loseReply) {
      loseReply = false;
      throw DioException(
        requestOptions: r,
        type: DioExceptionType.receiveTimeout,
      );
    }
    return f.backend.reply(201, {
      'revisionId': 'revision',
      'revisionNumber': pages[id]!['currentRevisionNumber'],
      'rowVersion': pages[id]!['rowVersion'],
      'replayed': false,
    });
  }
}

Future<void> settled(NotebookCubit notebook) async {
  for (
    var i = 0;
    i < 100 && (notebook.state.patientId == null || notebook.state.busy);
    i++
  ) {
    await Future<void>.delayed(const Duration(milliseconds: 2));
  }
  expect(notebook.state.busy, false);
}

void main() {
  late AuthFixture f;
  late NotebookServer server;
  late PatientContextCubit patients;
  late NotebookCubit notebook;
  setUp(() async {
    f = AuthFixture();
    server = NotebookServer(f);
    f.backend.patientSearch = (r) async =>
        page(f.backend, r, [patient('one'), patient('two')]);
    await f.signIn();
    patients = PatientContextCubit(f.cubit, PatientSearch(f.api));
    notebook = NotebookCubit(f.cubit, patients, NotebookApi(f.api));
    await patients.search('Patient');
    patients.select(patients.state.items.first);
    await settled(notebook);
  });
  tearDown(() async {
    await notebook.close();
    await patients.close();
    await f.cubit.close();
  });

  test('list success, empty, error and pagination', () async {
    expect(notebook.state.items.single.title, 'Page a');
    server.more = true;
    await notebook.refresh();
    await notebook.loadMore();
    expect(notebook.state.items.map((p) => p.id), ['a', 'b']);
    expect(notebook.state.page, 2);
    expect(notebook.state.hasMore, false);
    server.empty = true;
    await notebook.refresh();
    expect(notebook.state.items, isEmpty);
    server.fail = true;
    await notebook.refresh();
    expect(notebook.state.issue, NotebookIssue.failed);
  });
  test(
    'create title only, required validation, selects server projection',
    () async {
      await notebook.create(' ');
      expect(notebook.state.issue, NotebookIssue.title);
      await notebook.create('New page');
      expect(notebook.state.selected!.title, 'New page');
    },
  );
  test('open, revision, finalize read-only, amendment', () async {
    await notebook.open('a');
    final old = notebook.state.selected!.rowVersion;
    await notebook.submit();
    expect(notebook.state.selected!.revision, 1);
    expect(notebook.state.selected!.rowVersion, isNot(old));
    await notebook.finalize();
    expect(notebook.state.selected!.finalized, true);
    expect(notebook.canRevise, false);
    await notebook.submit();
    expect(server.mutations, 1);
    await notebook.submit(amendment: true);
    expect(server.mutations, 2);
    expect(notebook.state.selected!.revision, 2);
    expect(notebook.state.selected!.finalized, true);
    final forms = server.requests
        .where((r) => r.data is FormData)
        .map((r) => Map.fromEntries((r.data as FormData).fields))
        .toList();
    expect(forms[0]['originDeviceId'], forms[1]['originDeviceId']);
    expect(forms[0]['clientDraftId'], isNot(forms[1]['clientDraftId']));
    final body = utf8.decode(f.backend.multipartBodies.first);
    expect(
      body,
      contains('{"formatVersion":1,"patientId":"one","pageId":"a"}'),
    );
  });
  test('page_changed preserves state and blocks silent resubmission', () async {
    await notebook.open('a');
    final old = notebook.state.selected;
    server.stale = true;
    await notebook.submit();
    expect(notebook.state.issue, NotebookIssue.changed);
    expect(notebook.state.selected, same(old));
    expect(notebook.canRevise, false);
    final count = server.requests.length;
    await notebook.retry();
    expect(server.requests.length, count);
    server.stale = false;
    await notebook.refresh();
    expect(notebook.canRevise, true);
  });
  test(
    'lost reply retries identical draft and does not duplicate revisions',
    () async {
      await notebook.open('a');
      server.loseReply = true;
      await notebook.submit();
      final draft = notebook.state.pending!;
      expect(notebook.state.selected!.revision, 0);
      await notebook.retry();
      expect(server.mutations, 1);
      expect(notebook.state.selected!.revision, 1);
      final requests = server.requests
          .where((r) => r.data is FormData)
          .toList();
      expect(
        Map.fromEntries((requests[0].data as FormData).fields),
        Map.fromEntries((requests[1].data as FormData).fields),
      );
      expect(
        draft.bytes,
        utf8.encode('{"formatVersion":1,"patientId":"one","pageId":"a"}'),
      );
    },
  );
  test(
    'multipart refresh replays complete body with unchanged identifiers',
    () async {
      await notebook.open('a');
      f.backend.expiredAccess = true;
      await notebook.submit();
      expect(notebook.state.issue, isNull);
      expect(f.backend.refreshes, 1);
      expect(server.mutations, 1);
      expect(f.backend.multipartBodies, hasLength(2));
      for (final bytes in f.backend.multipartBodies) {
        expect(
          utf8.decode(bytes),
          contains('{"formatVersion":1,"patientId":"one","pageId":"a"}'),
        );
      }
    },
  );
  test(
    'patient switch clears detail and rejects late old patient response',
    () async {
      await notebook.open('a');
      server.gate = Completer<void>();
      final pending = notebook.refresh();
      patients.select(patients.state.items.last);
      await Future<void>.delayed(Duration.zero);
      expect(notebook.state.patientId, 'two');
      expect(notebook.state.selected, isNull);
      expect(notebook.state.items, isEmpty);
      server.gate!.complete();
      await pending;
      await settled(notebook);
      expect(notebook.state.items.every((p) => p.patientId == 'two'), true);
    },
  );
  test('logout clears state and late responses cannot restore it', () async {
    await notebook.open('a');
    server.gate = Completer<void>();
    final pending = notebook.refresh();
    await f.cubit.logout();
    await Future<void>.delayed(Duration.zero);
    expect(notebook.state.patientId, isNull);
    expect(notebook.state.selected, isNull);
    server.gate!.complete();
    await pending;
    expect(notebook.state.items, isEmpty);
  });
  test('session expiry clears notebook', () async {
    await notebook.open('a');
    f.backend.expiredAccess = true;
    f.backend.rejectRefresh = true;
    await notebook.refresh();
    await Future<void>.delayed(Duration.zero);
    expect(notebook.state.patientId, isNull);
    expect(notebook.state.items, isEmpty);
  });
  test('rejects wrong patient detail without publishing it', () async {
    server.pages['a'] = projection('two', 'a');
    await notebook.open('a');
    expect(notebook.state.issue, NotebookIssue.failed);
    expect(notebook.state.selected, isNull);
  });

  test(
    'backend denial clears cached notebook data and blocks writes',
    () async {
      await notebook.open('a');
      f.backend.notebook = (_) async => f.backend.reply(403, {});
      await notebook.submit();
      expect(notebook.state.issue, NotebookIssue.forbidden);
      expect(notebook.state.items, isEmpty);
      expect(notebook.state.selected, isNull);
      expect(notebook.canMutate, false);
    },
  );

  test('other 409 remains distinct from page_changed', () async {
    await notebook.open('a');
    final selected = notebook.state.selected;
    f.backend.notebook = (_) async =>
        f.backend.reply(409, {'code': 'draft_conflict'});
    await notebook.submit();
    expect(notebook.state.issue, NotebookIssue.conflict);
    expect(notebook.state.selected, same(selected));
  });

  for (final role in ['DoctorAssistant', 'Receptionist']) {
    testWidgets('$role notebook access follows session roles', (tester) async {
      final f = AuthFixture();
      f.backend.roles = [role];
      final server = NotebookServer(f);
      f.backend.patientSearch = (r) async =>
          page(f.backend, r, [patient('one')]);
      final signIn = f.signIn();
      await tester.pumpAndSettle();
      await signIn;
      await tester.pumpWidget(DoctorTabletApp(sessionCubit: f.cubit));
      await tester.pumpAndSettle();
      final context = tester.element(
        find.byKey(const Key('patient-search-term')),
      );
      final p = context.read<PatientContextCubit>();
      final search = p.search('Patient');
      await tester.pumpAndSettle();
      await search;
      p.select(p.state.items.first);
      await tester.pumpAndSettle();
      final n = context.read<NotebookCubit>();
      expect(n.canWrite, false);
      expect(find.byKey(const Key('notebook-create')), findsNothing);
      if (role == 'DoctorAssistant') {
        expect(find.text('دفتر الملاحظات'), findsOneWidget);
        final opening = n.open('a');
        await tester.pumpAndSettle();
        await opening;
        expect(n.state.selected, isNotNull);
        expect(find.byKey(const Key('notebook-finalize')), findsNothing);
        expect(find.byKey(const Key('notebook-revise')), findsNothing);
      } else {
        expect(server.requests, isEmpty);
        expect(find.text('دفتر الملاحظات'), findsNothing);
      }
      final count = server.requests.length;
      await n.create('New page');
      await n.submit();
      await n.finalize();
      expect(server.requests.length, count);
    });
  }

  for (final language in ['ar', 'en']) {
    testWidgets('notebook localized $language direction and persistent header', (
      tester,
    ) async {
      final f = AuthFixture();
      final widgetServer = NotebookServer(f);
      f.backend.patientSearch = (r) async =>
          page(f.backend, r, [patient('one')]);
      final signIn = f.signIn();
      await tester.pumpAndSettle();
      await signIn;
      await tester.pumpWidget(
        DoctorTabletApp(sessionCubit: f.cubit, initialLocale: Locale(language)),
      );
      await tester.pumpAndSettle();
      final context = tester.element(
        find.byKey(const Key('patient-search-term')),
      );
      final p = context.read<PatientContextCubit>();
      final search = p.search('Patient');
      await tester.pumpAndSettle();
      await search;
      p.select(p.state.items.first);
      await tester.pumpAndSettle();
      await tester.ensureVisible(find.byKey(const Key('notebook-create')));
      expect(
        find.text(language == 'ar' ? 'دفتر الملاحظات' : 'Notebook'),
        findsOneWidget,
      );
      expect(
        Directionality.of(
          tester.element(find.byKey(const Key('notebook-create'))),
        ),
        language == 'ar' ? TextDirection.rtl : TextDirection.ltr,
      );
      expect(find.byKey(const Key('patient-header')), findsOneWidget);
      final n = context.read<NotebookCubit>();
      final opening = n.open('a');
      await tester.pumpAndSettle();
      await opening;
      expect(find.byKey(const Key('notebook-revise')), findsOneWidget);
      final finalizing = n.finalize();
      await tester.pumpAndSettle();
      await finalizing;
      expect(
        n.state.selected!.finalized,
        true,
        reason:
            '${n.state.issue}, ${n.state.busy}, ${widgetServer.requests.map((r) => r.path).toList()}',
      );
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('notebook-revise')), findsNothing);
      expect(find.byKey(const Key('notebook-amend')), findsOneWidget);
      expect(tester.takeException(), isNull);
    });
  }
}
