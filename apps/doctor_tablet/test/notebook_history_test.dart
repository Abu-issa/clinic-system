import 'dart:async';
import 'dart:convert';

import 'package:dio/dio.dart';
import 'package:doctor_tablet/app/doctor_tablet_app.dart';
import 'package:doctor_tablet/features/notebook/data/notebook_api.dart';
import 'package:doctor_tablet/features/notebook/data/server_ink_codec.dart';
import 'package:doctor_tablet/features/notebook/ink/ink_document.dart';
import 'package:doctor_tablet/features/notebook/state/notebook_cubit.dart';
import 'package:doctor_tablet/features/patients/data/patient_search.dart';
import 'package:doctor_tablet/features/patients/state/patient_context_cubit.dart';
import 'package:flutter/material.dart';
import 'package:flutter/gestures.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';

import 'auth_fixture.dart';
import 'local_drafts_test.dart' show draw;
import 'notebook_test.dart' show NotebookServer, projection, settled;
import 'patient_context_test.dart' show patient, page;

class HistoryServer {
  HistoryServer(this.f) : notebook = NotebookServer(f) {
    notebook.more = true;
    notebook.pages['b'] = projection('one', 'b');
    f.backend.notebook = handle;
  }
  final AuthFixture f;
  final NotebookServer notebook;
  final requests = <RequestOptions>[];
  bool legacy = false,
      corrupt = false,
      wrongHeader = false,
      wrongPayload = false,
      wrongList = false;
  Completer<void>? payloadGate, entered;
  InkDocument document(String patientId, String pageId) => InkDocument(
    patientId: patientId,
    pageId: pageId,
    strokes: [
      InkStroke(
        id: 9,
        color: 0xff000000,
        width: .7,
        points: const [InkPoint(x: 30, y: 40, timeMicros: 15)],
      ),
    ],
  );
  Future<ResponseBody> handle(RequestOptions r) async {
    final parts = r.path.split('/');
    if (r.method == 'GET' &&
        (parts.last == 'revisions' || parts.last == 'payload')) {
      requests.add(r);
      final patientId = parts[4], pageId = parts[7];
      if (parts.last == 'revisions') {
        final pageNumber = r.queryParameters['page'] as int;
        final numbers = pageNumber == 1
            ? List.generate(20, (i) => 22 - i)
            : [2, 1];
        return f.backend.reply(200, {
          'patientId': wrongList ? 'other' : patientId,
          'pageId': pageId,
          'page': pageNumber,
          'pageSize': 20,
          'hasMore': pageNumber == 1,
          'currentRevisionNumber': 22,
          'items': [
            for (final n in numbers)
              {
                'revisionNumber': n,
                'authorStaffId': 'doctor-author',
                'kind': n == 1
                    ? 'Created'
                    : n == 22
                    ? 'Amendment'
                    : 'Revision',
                'hasPayload': n != 1,
                'createdAtUtc': '2026-09-27T10:00:00Z',
              },
          ],
        });
      }
      if (entered != null && !entered!.isCompleted) entered!.complete();
      await payloadGate?.future;
      final bytes = corrupt
          ? [1, 2, 3]
          : legacy
          ? utf8.encode(
              ' \n${jsonEncode({'formatVersion': 1, 'patientId': patientId, 'pageId': pageId})}',
            )
          : encodeServerInk(
              document(wrongPayload ? 'other' : patientId, pageId),
            );
      return ResponseBody.fromBytes(
        bytes,
        200,
        headers: {
          'x-notebook-patient': [patientId],
          'x-notebook-page': [pageId],
          'x-notebook-revision': [wrongHeader ? '999' : parts[9]],
        },
      );
    }
    return notebook.handle(r);
  }
}

void main() {
  late AuthFixture f;
  late HistoryServer server;
  late PatientContextCubit patients;
  late NotebookCubit n;
  setUp(() async {
    f = AuthFixture();
    server = HistoryServer(f);
    f.backend.patientSearch = (r) async =>
        page(f.backend, r, [patient('one'), patient('two')]);
    await f.signIn();
    patients = PatientContextCubit(f.cubit, PatientSearch(f.api));
    n = NotebookCubit(f.cubit, patients, NotebookApi(f.api));
    await patients.search('Patient');
    patients.select(patients.state.items.first);
    await settled(n);
    await n.open('a');
  });
  tearDown(() async {
    await n.close();
    await patients.close();
    await f.cubit.close();
  });

  test('open history and paginate metadata without reading payloads', () async {
    await n.showHistory();
    expect(n.history.visible, isTrue);
    expect(n.history.items, hasLength(20));
    expect(n.history.items.first.number, 22);
    expect(n.history.items.first.kind, 'Amendment');
    expect(n.history.items.first.author, 'doctor-author');
    await n.history.loadMore();
    expect(n.history.items, hasLength(22));
    expect(n.history.hasMore, isFalse);
    expect(server.requests.every((r) => r.path.endsWith('/revisions')), isTrue);
  });
  test('MessagePack historical vector is separate from the current dirty draft and history', () async {
    final draft = n.activeDraft!;
    draw(draft);
    final snapshot = draft.ink.document, version = draft.rowVersion;
    final writes = f.drafts.writes;
    await n.showHistory();
    await n.history.select(n.history.items.first);
    expect(n.history.ink!.document.strokes.single.points.single.x, 30);
    expect(n.activeDraft, same(draft));
    expect(draft.ink.document, same(snapshot));
    expect(draft.rowVersion, version);
    expect(draft.dirty, isTrue);
    expect(draft.ink.canUndo, isTrue);
    expect(f.drafts.writes, writes);
    expect(await f.drafts.queued(draft.key.owner), isEmpty);
    expect(n.canDraw, isFalse);
    expect(n.canFinalize, isFalse);
    n.history.closeView();
    expect(n.history.ink, isNull);
    expect(n.history.items, isEmpty);
    expect(n.activeDraft, same(draft));
    expect(draft.ink.document, same(snapshot));
    expect(n.canDraw, isTrue);
  });
  test('legacy v1 metadata-only payload including whitespace is an empty labelled revision', () async {
    server.legacy = true;
    await n.showHistory();
    await n.history.select(n.history.items.first);
    expect(n.history.failed, isFalse);
    expect(n.history.ink!.legacy, isTrue);
    expect(n.history.ink!.document.strokes, isEmpty);
  });
  test('Created revision has no private payload and opens an empty historical page', () async {
    await n.showHistory();
    await n.history.loadMore();
    final requests = server.requests.length;
    await n.history.select(n.history.items.last);
    expect(n.history.selected!.kind, 'Created');
    expect(n.history.ink!.document.strokes, isEmpty);
    expect(server.requests.length, requests);
  });
  for (final failure in ['corrupt', 'header', 'payload', 'list']) {
    test(
      '$failure historical failure is safe and leaves draft untouched',
      () async {
        final draft = n.activeDraft!;
        draw(draft);
        final document = draft.ink.document;
        server.corrupt = failure == 'corrupt';
        server.wrongHeader = failure == 'header';
        server.wrongPayload = failure == 'payload';
        server.wrongList = failure == 'list';
        await n.showHistory();
        if (failure != 'list') await n.history.select(n.history.items.first);
        expect(n.history.failed, isTrue);
        expect(n.history.ink, isNull);
        expect(draft.ink.document, same(document));
        expect(draft.dirty, isTrue);
        expect(f.drafts.writes, 0);
      },
    );
  }
  for (final state in ['queued', 'conflict']) {
    test(
      '$state current work survives browsing without queue or conflict mutations',
      () async {
        final draft = n.activeDraft!;
        draw(draft);
        await n.queue!.enqueue(
          draft,
          n.state.selected!,
          n.originDeviceId,
          amendment: false,
        );
        if (state == 'conflict') {
          server.notebook.stale = true;
          await n.queue!.drain();
        }
        final queue = (await f.drafts.queued(draft.key.owner))
            .map((q) => Map.of(q.row))
            .toList();
        final document = draft.ink.document, version = draft.rowVersion;
        final writes = f.drafts.writes;
        await n.showHistory();
        await n.history.select(n.history.items.first);
        expect(draft.syncState, state);
        expect(draft.ink.document, same(document));
        expect(draft.rowVersion, version);
        expect(f.drafts.writes, writes);
        expect(
          (await f.drafts.queued(draft.key.owner)).map((q) => q.row).toList(),
          queue,
        );
        n.history.closeView();
        expect(n.activeDraft, same(draft));
      },
    );
  }
  test('historical amendment cannot alter current finalized lifecycle or amendment mode', () async {
    await n.loadMore();
    await n.open('b');
    server.notebook.pages['a'] = projection('one', 'a', finalized: true);
    await n.open('a');
    n.beginAmendment();
    expect(n.editingAmendment, isTrue);
    await n.showHistory();
    await n.history.select(n.history.items.first);
    expect(n.history.selected!.kind, 'Amendment');
    expect(n.canDraw, isFalse);
    expect(n.editingAmendment, isTrue);
    n.history.closeView();
    expect(n.editingAmendment, isTrue);
    expect(n.canDraw, isTrue);
  });
  for (final change in ['page', 'patient', 'account', 'back']) {
    test(
      '$change switch cancels a late history response and releases its ink',
      () async {
        await n.loadMore();
        await n.showHistory();
        server.entered = Completer<void>();
        server.payloadGate = Completer<void>();
        final reading = n.history.select(n.history.items.first);
        await server.entered!.future;
        if (change == 'page') {
          await n.open('b');
        }
        if (change == 'patient') {
          patients.select(patients.state.items.last);
          await settled(n);
        }
        if (change == 'account') {
          await f.cubit.logout();
          await Future<void>.delayed(Duration.zero);
        }
        if (change == 'back') {
          n.history.closeView();
        }
        server.payloadGate!.complete();
        await reading;
        expect(n.history.visible, isFalse);
        expect(n.history.ink, isNull);
        expect(n.history.items, isEmpty);
      },
    );
  }
  test(
    'selecting another historical revision releases old payload immediately',
    () async {
      await n.showHistory();
      await n.history.select(n.history.items.first);
      expect(n.history.ink, isNotNull);
      server.payloadGate = Completer<void>();
      server.entered = Completer<void>();
      final loading = n.history.select(n.history.items[1]);
      await server.entered!.future;
      expect(n.history.ink, isNull);
      expect(n.history.selected!.number, 21);
      server.payloadGate!.complete();
      await loading;
      expect(n.history.ink, isNotNull);
    },
  );

  test(
    'history cannot interrupt an active stylus contact or its undo history',
    () async {
      final draft = n.activeDraft!;
      draft.ink.handle(
        const PointerDownEvent(
          pointer: 5,
          kind: PointerDeviceKind.stylus,
          position: Offset(20, 20),
        ),
        const Size(210, 297),
        patientId: 'one',
        pageId: 'a',
        enabled: true,
      );
      await n.showHistory();
      expect(n.history.visible, isFalse);
      expect(draft.ink.active.points, hasLength(1));
      expect(draft.ink.canUndo, isFalse);
      draft.ink.cancel();
    },
  );

  for (final language in ['ar', 'en']) {
    testWidgets(
      '$language history labels, immutable canvas, visible patient and exact return',
      (tester) async {
        final wf = AuthFixture();
        HistoryServer(wf).legacy = true;
        wf.backend.patientSearch = (r) async =>
            page(wf.backend, r, [patient('one')]);
        final signing = wf.signIn();
        await tester.pumpAndSettle();
        await signing;
        await tester.pumpWidget(
          DoctorTabletApp(
            sessionCubit: wf.cubit,
            initialLocale: Locale(language),
          ),
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
        final book = context.read<NotebookCubit>();
        final opening = book.open('a');
        await tester.pumpAndSettle();
        await opening;
        final draft = book.activeDraft!;
        draw(draft);
        final snapshot = draft.ink.document;
        await tester.pump();
        await Scrollable.ensureVisible(
          tester.element(find.byKey(const Key('notebook-history'))),
          alignment: .5,
        );
        await tester.pumpAndSettle();
        await tester.tap(find.byKey(const Key('notebook-history')));
        await tester.pumpAndSettle();
        expect(find.byKey(const Key('history-list')), findsOneWidget);
        expect(
          Directionality.of(
            tester.element(find.byKey(const Key('history-list'))),
          ),
          language == 'ar' ? TextDirection.rtl : TextDirection.ltr,
        );
        final selecting = book.history.select(book.history.items.first);
        await tester.pumpAndSettle();
        await selecting;
        expect(find.byKey(const Key('history-read-only')), findsOneWidget);
        expect(
          find.text(
            language == 'ar'
                ? 'للقراءة فقط / إصدار سابق'
                : 'READ ONLY / HISTORICAL REVISION',
          ),
          findsOneWidget,
        );
        expect(
          find.text(
            language == 'ar'
                ? 'العودة إلى الصفحة الحالية'
                : 'Back to Current Page',
          ),
          findsOneWidget,
        );
        expect(find.byKey(const Key('history-legacy')), findsOneWidget);
        expect(find.byKey(const Key('history-current-status')), findsOneWidget);
        expect(find.byKey(const Key('patient-header')), findsOneWidget);
        expect(find.byKey(const Key('notebook-revise')), findsNothing);
        final canvas = find.byKey(const Key('history-canvas'));
        await Scrollable.ensureVisible(tester.element(canvas), alignment: .5);
        await tester.pumpAndSettle();
        final pointer = await tester.startGesture(
          tester.getCenter(canvas),
          kind: PointerDeviceKind.stylus,
        );
        await pointer.moveBy(const Offset(5, 5));
        await pointer.up();
        await tester.pump();
        expect(draft.ink.document, same(snapshot));
        expect(draft.ink.canUndo, isTrue);
        book.history.closeView();
        await tester.pumpAndSettle();
        expect(book.activeDraft, same(draft));
        expect(draft.ink.document, same(snapshot));
        expect(find.byKey(const Key('history-canvas')), findsNothing);
        await tester.pumpWidget(const SizedBox());
        await tester.pumpAndSettle();
      },
    );
  }
}
