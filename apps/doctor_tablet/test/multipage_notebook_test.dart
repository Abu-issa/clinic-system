import 'dart:async';

import 'package:doctor_tablet/app/doctor_tablet_app.dart';
import 'package:doctor_tablet/features/notebook/data/notebook_api.dart';
import 'package:doctor_tablet/features/notebook/state/notebook_cubit.dart';
import 'package:doctor_tablet/features/patients/data/patient_search.dart';
import 'package:doctor_tablet/features/patients/state/patient_context_cubit.dart';
import 'package:flutter/material.dart';
import 'package:flutter/gestures.dart';
import 'package:dio/dio.dart';
import 'package:doctor_tablet/features/notebook/data/server_ink_codec.dart';
import 'package:doctor_tablet/features/notebook/ink/ink_document.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';

import 'auth_fixture.dart';
import 'local_drafts_test.dart' show draw;
import 'notebook_test.dart' show NotebookServer, projection, settled;
import 'patient_context_test.dart' show patient, page;

void main() {
  late AuthFixture f;
  late NotebookServer server;
  late PatientContextCubit patients;
  late NotebookCubit n;
  setUp(() async {
    f = AuthFixture();
    server = NotebookServer(f)..more = true;
    server.pages['b'] = projection('one', 'b');
    f.backend.patientSearch = (r) async =>
        page(f.backend, r, [patient('one'), patient('two')]);
    await f.signIn();
    patients = PatientContextCubit(f.cubit, PatientSearch(f.api));
    n = NotebookCubit(f.cubit, patients, NotebookApi(f.api));
    await patients.search('Patient');
    patients.select(patients.state.items.first);
    await settled(n);
  });
  tearDown(() async {
    f.drafts.fail = false;
    await n.close();
    await patients.close();
    await f.cubit.close();
  });

  test(
    'progressive list reads metadata only; next loads more in server order',
    () async {
      expect(n.state.items.map((p) => p.id), ['a']);
      expect(n.state.hasMore, isTrue);
      expect(f.drafts.reads, isEmpty);
      await n.open('a');
      await n.next();
      expect(n.state.items.map((p) => p.id), ['a', 'b']);
      expect(n.state.selected!.id, 'b');
      expect(n.state.hasMore, isFalse);
      expect(n.selectedIndex, 1);
      await n.previous();
      expect(n.state.selected!.id, 'a');
      expect(n.hasPrevious, isFalse);
      expect(
        server.requests.any((r) => r.path.contains('/revisions/')),
        isFalse,
      );
    },
  );

  test(
    'A B A restores separate encrypted drafts and disposes inactive history',
    () async {
      await n.loadMore();
      await n.open('a');
      final a = n.activeDraft!;
      draw(a);
      expect(a.ink.canUndo, isTrue);
      await n.open('b');
      expect(a.disposed, isTrue);
      expect(f.cubit.drafts.find(a.key), isNull);
      final b = n.activeDraft!;
      expect(b.ink.document.strokes, isEmpty);
      draw(b);
      draw(b, pointer: 2);
      await n.open('a');
      expect(b.disposed, isTrue);
      expect(n.activeDraft, isNot(same(a)));
      expect(n.activeDraft!.ink.document.strokes, hasLength(1));
      expect(n.activeDraft!.ink.canUndo, isFalse);
      await n.open('b');
      expect(n.activeDraft!.ink.document.strokes, hasLength(2));
    },
  );

  test(
    'switch finishes a partial stylus contact before saving and releasing',
    () async {
      await n.loadMore();
      await n.open('a');
      final a = n.activeDraft!;
      a.ink.handle(
        const PointerDownEvent(
          pointer: 9,
          kind: PointerDeviceKind.stylus,
          position: Offset(21, 35),
        ),
        const Size(210, 297),
        patientId: 'one',
        pageId: 'a',
        enabled: true,
      );
      expect(a.ink.active.points, hasLength(1));
      await n.open('b');
      expect(a.disposed, isTrue);
      expect((await f.drafts.read(a.key))!.document.strokes, hasLength(1));
      expect(n.activeDraft!.ink.active.points, isEmpty);
    },
  );

  test(
    'only selected latest revision hydrates; reopening prefers its local draft',
    () async {
      server.pages['a'] = projection('one', 'a', revision: 3);
      final document = InkDocument(
        patientId: 'one',
        pageId: 'a',
        strokes: [
          InkStroke(
            id: 1,
            color: 0xff000000,
            width: .7,
            points: const [InkPoint(x: 4, y: 6, timeMicros: 0)],
          ),
        ],
      );
      f.backend.notebook = (r) async {
        if (r.path.endsWith('/payload')) {
          server.requests.add(r);
          return ResponseBody.fromBytes(encodeServerInk(document), 200);
        }
        return server.handle(r);
      };
      await n.refresh();
      await n.loadMore();
      expect(
        server.requests.where((r) => r.path.endsWith('/payload')),
        isEmpty,
      );
      await n.open('a');
      expect(n.activeDraft!.ink.document.strokes.single.points.single.x, 4);
      expect(n.activeDraft!.serverSynced, isTrue);
      await n.open('b');
      await n.open('a');
      expect(
        server.requests.where((r) => r.path.endsWith('/payload')),
        hasLength(1),
      );
      expect(n.activeDraft!.ink.document.strokes, hasLength(1));
    },
  );

  test(
    'failed latest server ink read never exposes an editable blank page',
    () async {
      server.pages['a'] = projection('one', 'a', revision: 2);
      f.backend.notebook = (r) async => r.path.endsWith('/payload')
          ? f.backend.reply(503, {})
          : server.handle(r);
      await n.loadMore();
      await n.open('a');
      expect(n.state.selected, isNull);
      expect(n.canDraw, isFalse);
      expect(f.drafts.rows, isEmpty);
      await n.open('b');
      expect(n.state.selected!.id, 'b');
    },
  );

  test(
    'different staff cannot see prior page badges, drafts or queue',
    () async {
      await n.open('a');
      draw(n.activeDraft!);
      server.stale = true;
      await n.submit();
      final oldOwner = f.cubit.draftOwner!;
      expect(await f.cubit.logout(), isTrue);
      f.backend.staffId = 'other-doctor';
      await f.signIn();
      await Future<void>.delayed(
        Duration.zero,
      ); // Deliver authenticated session to patient context.
      await patients.search('Patient');
      patients.select(patients.state.items.first);
      await settled(n);
      expect(f.cubit.draftOwner, isNot(oldOwner));
      expect(n.state.localStates['a'], isNull);
      await n.open('a');
      expect(n.activeDraft!.ink.document.strokes, isEmpty);
      expect(n.activeDraft!.syncState, isNot('conflict'));
      expect(await f.drafts.queued(oldOwner), hasLength(1));
    },
  );

  test(
    'failed switch save keeps current unsaved ink and does not fetch target',
    () async {
      await n.loadMore();
      await n.open('a');
      final a = n.activeDraft!;
      draw(a);
      f.drafts.fail = true;
      final count = server.requests.length;
      await n.open('b');
      expect(n.state.selected!.id, 'a');
      expect(n.activeDraft, same(a));
      expect(a.dirty, isTrue);
      expect(a.canEdit, isTrue);
      expect(server.requests.length, count);
      f.drafts.fail = false;
      await n.open('b');
      expect(n.state.selected!.id, 'b');
    },
  );

  test(
    'create is server authoritative, single flight, prepends and selects',
    () async {
      await n.open('a');
      draw(n.activeDraft!);
      server.gate = Completer<void>();
      final first = n.create('New page');
      final second = n.create('New page');
      await Future<void>.delayed(const Duration(milliseconds: 10));
      server.gate!.complete();
      await Future.wait([first, second]);
      expect(
        server.requests.where(
          (r) => r.method == 'POST' && r.path.endsWith('/pages'),
        ),
        hasLength(1),
      );
      expect(n.state.items.map((p) => p.id), ['new', 'a']);
      expect(n.state.selected!.id, 'new');
      expect(n.activeDraft!.ink.document.strokes, isEmpty);
      expect(f.drafts.rows.keys.map((k) => k.pageId), contains('a'));
    },
  );

  test(
    'finalized and draft pages keep independent lifecycle and amendment mode',
    () async {
      server.pages['a'] = projection('one', 'a', finalized: true, revision: 2);
      await n.loadMore();
      await n.open('a');
      expect(n.canDraw, isFalse);
      expect(n.canAmend, isTrue);
      n.beginAmendment();
      expect(n.canDraw, isTrue);
      await n.open('b');
      expect(n.canDraw, isTrue);
      expect(n.editingAmendment, isFalse);
      await n.open('a');
      expect(n.canDraw, isFalse);
      expect(n.canAmend, isTrue);
    },
  );

  test(
    'conflict badge survives navigation; B syncs while A remains stopped',
    () async {
      await n.loadMore();
      await n.open('a');
      draw(n.activeDraft!);
      server.stale = true;
      await n.submit();
      final before = (await f.drafts.queued(f.cubit.draftOwner!)).single;
      expect(n.state.localStates['a'], 'conflict');
      expect(n.canFinalize, isFalse);
      server.stale = false;
      await n.open('b');
      draw(n.activeDraft!);
      await n.submit();
      expect(n.state.localStates['b'], 'serverSynced');
      expect(n.state.localStates['a'], 'conflict');
      final after = (await f.drafts.queued(f.cubit.draftOwner!)).single;
      expect(after.id, before.id);
      expect(after.attempts, before.attempts);
      await n.open('a');
      expect(n.activeDraft!.syncState, 'conflict');
      expect(n.activeDraft!.ink.document.strokes, hasLength(1));
      expect(n.canDraw, isFalse);
      expect(n.canCreate, isTrue);
    },
  );

  test(
    'reopening notebook reads conflict badge without loading inactive ink',
    () async {
      await n.open('a');
      draw(n.activeDraft!);
      server.stale = true;
      await n.submit();
      await n.close();
      f.drafts.reads.clear();
      n = NotebookCubit(f.cubit, patients, NotebookApi(f.api));
      await settled(n);
      expect(n.state.localStates['a'], 'conflict');
      expect(n.activeDraft, isNull);
      expect(f.drafts.reads, isEmpty);
      await n.open('a');
      expect(n.activeDraft!.syncState, 'conflict');
    },
  );

  test('foreground ACK for an inactive page updates its badge without replacing the canvas', () async {
    await n.loadMore();
    await n.open('b');
    final b = n.activeDraft!;
    draw(b);
    await n.queue!.enqueue(
      b,
      n.state.selected!,
      n.originDeviceId,
      amendment: false,
    );
    expect(n.state.localStates['b'], 'queued');
    await n.open('a');
    final a = n.activeDraft!;
    await n.queue!.drain();
    expect(n.activeDraft, same(a));
    expect(n.state.selected!.id, 'a');
    expect(n.state.items.firstWhere((p) => p.id == 'b').revision, 1);
    expect(n.state.localStates['b'], 'serverSynced');
    expect(f.cubit.drafts.find(b.key), isNull);
  });

  test(
    'late old-patient page response cannot replace the new patient workspace',
    () async {
      await n.loadMore();
      await n.open('a');
      draw(n.activeDraft!);
      final old = n.activeDraft!;
      final entered = Completer<void>(), gate = Completer<void>();
      f.backend.notebook = (r) async {
        if (r.path.endsWith('/one/notebook/pages/b')) {
          entered.complete();
          await gate.future;
        }
        return server.handle(r);
      };
      final opening = n.open('b');
      await entered.future;
      patients.select(patients.state.items.last);
      await settled(n);
      gate.complete();
      await opening;
      expect(n.state.patientId, 'two');
      expect(n.state.selected, isNull);
      expect(n.activeDraft, isNull);
      expect(old.disposed, isTrue);
      expect((await f.drafts.read(old.key))!.document.strokes, hasLength(1));
      expect(n.state.items.every((p) => p.patientId == 'two'), isTrue);
    },
  );

  test(
    'logout refuses failed unsaved page then retains all saved page drafts',
    () async {
      await n.loadMore();
      await n.open('a');
      draw(n.activeDraft!);
      await n.open('b');
      draw(n.activeDraft!);
      f.drafts.fail = true;
      expect(await f.cubit.logout(), isFalse);
      expect(n.activeDraft!.ink.document.pageId, 'b');
      f.drafts.fail = false;
      expect(await f.cubit.logout(), isTrue);
      expect(f.drafts.rows.keys.map((k) => k.pageId), containsAll(['a', 'b']));
      expect(n.activeDraft, isNull);
    },
  );

  for (final language in ['ar', 'en']) {
    testWidgets(
      '$language bounded page list, position, navigation, badges and header',
      (tester) async {
        final wf = AuthFixture();
        final ws = NotebookServer(wf)..more = true;
        ws.pages['b'] = projection('one', 'b');
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
        Future<void> finish(Future<void> action) async {
          await tester.pumpAndSettle();
          await action;
        }

        await finish(book.open('a'));
        draw(book.activeDraft!);
        await tester.pump();
        expect(
          find.text(language == 'ar' ? 'صفحات الدفتر' : 'Notebook pages'),
          findsOneWidget,
        );
        final list = find.byKey(const Key('notebook-page-list'));
        expect(tester.getSize(list).height, 260);
        expect(
          Directionality.of(tester.element(list)),
          language == 'ar' ? TextDirection.rtl : TextDirection.ltr,
        );
        expect(find.byKey(const Key('notebook-status-a')), findsOneWidget);
        await Scrollable.ensureVisible(
          tester.element(find.byKey(const Key('notebook-next'))),
          alignment: .5,
        );
        await tester.pumpAndSettle();
        await tester.tap(find.byKey(const Key('notebook-next')));
        await tester.pumpAndSettle();
        expect(book.state.selected!.id, 'b');
        expect(book.state.items.map((p) => p.id), ['a', 'b']);
        expect(
          find.text(language == 'ar' ? 'الصفحة 2 من 2' : 'Page 2 of 2'),
          findsOneWidget,
        );
        expect(find.byKey(const Key('patient-header')), findsOneWidget);
        expect(tester.takeException(), isNull);
        await tester.pumpWidget(const SizedBox());
        await tester.pumpAndSettle();
      },
    );
  }
}
