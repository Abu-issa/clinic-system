import 'dart:async';
import 'dart:io';
import 'dart:ui' as ui;

import 'package:doctor_tablet/features/notebook/data/local_ink_draft.dart';
import 'package:doctor_tablet/features/notebook/data/notebook_api.dart';
import 'package:doctor_tablet/features/notebook/data/server_ink_codec.dart';
import 'package:doctor_tablet/features/notebook/ink/ink_controller.dart';
import 'package:doctor_tablet/features/notebook/ink/ink_viewport.dart';
import 'package:doctor_tablet/features/notebook/presentation/ink_page.dart';
import 'package:doctor_tablet/features/notebook/presentation/stylus_diagnostics.dart';
import 'package:doctor_tablet/features/notebook/state/local_drafts.dart';
import 'package:doctor_tablet/features/notebook/state/notebook_sync_queue.dart';
import 'package:doctor_tablet/features/notebook/state/notebook_cubit.dart';
import 'package:doctor_tablet/features/patients/data/patient_search.dart';
import 'package:doctor_tablet/features/patients/state/patient_context_cubit.dart';
import 'package:flutter/material.dart';
import 'package:flutter/gestures.dart';
import 'package:flutter_test/flutter_test.dart';

import 'auth_fixture.dart';
import 'draft_fixture.dart';
import 'notebook_test.dart' show NotebookServer, projection, settled;
import 'patient_context_test.dart' show patient, page;
import 'stress_fixture.dart';

import 'package:doctor_tablet/features/notebook/data/notebook_history_api.dart';
import 'package:doctor_tablet/features/notebook/state/notebook_history.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  test(
    'dense page A B A switches release controllers and preserve every point',
    () async {
      final f = AuthFixture();
      final server = NotebookServer(f);
      await f.signIn();
      f.backend.patientSearch = (r) async =>
          page(f.backend, r, [patient('one')]);
      server.pages['dense-a'] = projection('one', 'dense-a');
      server.pages['dense-b'] = projection('one', 'dense-b');
      f.backend.notebook = (r) async {
        if (r.method == 'GET' && r.path.endsWith('/pages')) {
          return f.backend.reply(200, {
            'items': server.pages.values.toList(),
            'page': 1,
            'pageSize': 10,
            'hasMore': false,
          });
        }
        return server.handle(r);
      };
      final patients = PatientContextCubit(f.cubit, PatientSearch(f.api));
      final book = NotebookCubit(f.cubit, patients, NotebookApi(f.api));
      await patients.search('Patient');
      patients.select(patients.state.items.first);
      await settled(book);
      DraftHandle? previous;
      final watch = Stopwatch()..start();
      final rss = <int>[];
      for (var pass = 0; pass < 3; pass++) {
        for (final id in ['dense-a', 'dense-b']) {
          await book.open(id);
          if (previous != null) expect(previous.disposed, isTrue);
          final current = book.activeDraft!;
          if (pass == 0) {
            current.ink.restore(stressInk(patient: 'one', page: id));
          }
          expect(current.ink.document.pageId, id);
          expect(current.ink.document.strokes.length, 1000);
          expect(
            current.ink.document.strokes.fold<int>(
              0,
              (n, s) => n + s.points.length,
            ),
            50000,
          );
          previous = current;
          rss.add(ProcessInfo.currentRss);
        }
      }
      // ignore: avoid_print
      print('STRESS dense_6_switches_ms=${watch.elapsedMilliseconds} rss=$rss');
      await book.close();
      expect(previous!.disposed, isTrue);
      await patients.close();
      await f.cubit.close();
    },
  );
  test('50 pages switch through notebook ownership twice without retained inactive controllers', () async {
    final f = AuthFixture();
    final server = NotebookServer(f);
    await f.signIn();
    f.backend.patientSearch = (r) async => page(f.backend, r, [patient('one')]);
    for (var i = 0; i < 50; i++) {
      server.pages['stress-$i'] = projection('one', 'stress-$i');
    }
    f.backend.notebook = (r) async {
      if (r.method == 'GET' && r.path.endsWith('/pages')) {
        final index = r.queryParameters['page'] as int;
        return f.backend.reply(200, {
          'items': [
            for (var i = (index - 1) * 10; i < index * 10 && i < 50; i++)
              server.pages['stress-$i'],
          ],
          'page': index,
          'pageSize': 10,
          'hasMore': index < 5,
        });
      }
      return server.handle(r);
    };
    final patients = PatientContextCubit(f.cubit, PatientSearch(f.api));
    final book = NotebookCubit(f.cubit, patients, NotebookApi(f.api));
    await patients.search('Patient');
    patients.select(patients.state.items.first);
    await settled(book);
    while (book.state.hasMore) {
      await book.loadMore();
    }
    final before = ProcessInfo.currentRss, watch = Stopwatch()..start();
    DraftHandle? previous;
    for (var pass = 0; pass < 2; pass++) {
      for (var i = 0; i < 50; i++) {
        await book.open('stress-$i');
        if (previous != null) expect(previous.disposed, isTrue);
        previous = book.activeDraft!;
        if (pass == 0) {
          previous.ink.restore(
            stressInk(patient: 'one', page: 'stress-$i', strokes: 2),
          );
        }
        expect(previous.ink.document.strokes.length, 2);
      }
    }
    // ignore: avoid_print
    print(
      'STRESS notebook_100_switches_ms=${watch.elapsedMilliseconds} rss_delta_bytes=${ProcessInfo.currentRss - before}',
    );
    await book.close();
    await patients.close();
    await f.cubit.close();
  });
  test('5000 historical metadata records load progressively then release without payload reads', () async {
    final f = AuthFixture();
    await f.signIn();
    var requests = 0;
    f.backend.notebook = (r) async {
      expect(r.path.endsWith('/revisions'), isTrue);
      requests++;
      final page = r.queryParameters['page'] as int;
      return f.backend.reply(200, {
        'patientId': 'one',
        'pageId': 'a',
        'page': page,
        'pageSize': 20,
        'currentRevisionNumber': 5000,
        'hasMore': page < 250,
        'items': [
          for (var i = 0; i < 20; i++)
            {
              'revisionNumber': 5000 - (page - 1) * 20 - i,
              'kind': 'Revision',
              'authorStaffId': 'synthetic',
              'createdAtUtc': '2026-01-01T00:00:00Z',
              'hasPayload': true,
            },
        ],
      });
    };
    final history = NotebookHistory(
      NotebookHistoryApi(f.api),
      () => DraftKey(f.cubit.draftOwner!, 'one', 'a'),
    );
    final watch = Stopwatch()..start();
    await history.open();
    while (history.hasMore) {
      await history.loadMore();
    }
    expect(history.items.length, 5000);
    expect(requests, 250);
    expect(history.ink, isNull);
    // ignore: avoid_print
    print('STRESS history_5000_metadata_ms=${watch.elapsedMilliseconds}');
    history.closeView();
    expect(history.items, isEmpty);
    history.dispose();
    await f.cubit.close();
  });
  test('synthetic 1000 strokes 50000 points codec render recording undo and pan stress', () {
    final before = ProcessInfo.currentRss;
    final document = stressInk();
    final watch = Stopwatch()..start();
    final bytes = encodeServerInk(document);
    final encode = watch.elapsedMicroseconds;
    watch.reset();
    final restored = decodeServerInk(
      bytes,
      document.patientId,
      document.pageId,
    );
    final decode = watch.elapsedMicroseconds;
    watch.reset();
    final local = encodeLocalDraft(
      LocalInkDraft(
        key: DraftKey('synthetic-owner', document.patientId, document.pageId),
        document: document,
        updatedAt: DateTime.utc(2026),
      ),
    );
    final localEncode = watch.elapsedMicroseconds;
    watch.reset();
    expect(
      decodeLocalDraft((
        DraftKey('synthetic-owner', document.patientId, document.pageId),
        local,
      )).document.strokes.length,
      1000,
    );
    final localDecode = watch.elapsedMicroseconds;
    watch.reset();
    final painter = InkPainter(restored.strokes);
    for (var i = 0; i < 20; i++) {
      final recorder = ui.PictureRecorder();
      painter.paint(Canvas(recorder), const Size(840, 1188));
      recorder.endRecording().dispose();
    }
    final recording = watch.elapsedMicroseconds;
    final controller = InkController(document.patientId, document.pageId)
      ..restore(document);
    final viewport = InkViewport();
    var commits = 0;
    controller.addListener(() => commits++);
    controller.handle(
      const PointerDownEvent(
        pointer: 1,
        kind: PointerDeviceKind.stylus,
        position: Offset(1, 1),
      ),
      const Size(210, 297),
      patientId: document.patientId,
      pageId: document.pageId,
      enabled: true,
    );
    watch.reset();
    for (var i = 1; i < 20000; i++) {
      controller.handle(
        PointerMoveEvent(
          pointer: 1,
          kind: PointerDeviceKind.stylus,
          position: Offset((i % 200).toDouble(), 20),
        ),
        const Size(210, 297),
        patientId: document.patientId,
        pageId: document.pageId,
        enabled: true,
      );
    }
    final active = watch.elapsedMicroseconds;
    expect(commits, 0);
    expect(controller.document, same(document));
    controller.finishActive();
    for (var i = 0; i < 500; i++) {
      controller.undo();
      controller.redo();
    }
    final snapshot = controller.document;
    for (var i = 0; i < 1000; i++) {
      viewport.handle(
        const PointerDownEvent(pointer: 2, position: Offset(40, 40)),
        const Size(210, 297),
      );
      viewport.handle(
        const PointerDownEvent(pointer: 3, position: Offset(80, 80)),
        const Size(210, 297),
      );
      viewport.handle(
        const PointerMoveEvent(pointer: 3, position: Offset(90, 90)),
        const Size(210, 297),
      );
      viewport.reset();
    }
    expect(controller.document, same(snapshot));
    expect(snapshot.strokes.length, 1001);
    // Test-only output: numeric measurements of synthetic content, no payloads.
    // ignore: avoid_print
    print(
      'STRESS codec_bytes=${bytes.length} encode_us=$encode decode_us=$decode local_encode_us=$localEncode local_decode_us=$localDecode picture_record_20_us=$recording active_20000_us=$active rss_delta_bytes=${ProcessInfo.currentRss - before}',
    );
    controller.dispose();
    viewport.dispose();
  });

  test('100 pages repeated open close save failure pause overlap and retained restart', () async {
    final store = MemoryDraftStore();
    final drafts = LocalDrafts(store);
    final watch = Stopwatch()..start();
    for (var round = 0; round < 3; round++) {
      for (var i = 0; i < 100; i++) {
        final key = DraftKey('synthetic-owner', 'synthetic-patient', 'page-$i');
        final handle = drafts.open(key);
        await handle.ready;
        if (round == 0) {
          handle.ink.restore(stressInk(page: key.pageId, strokes: 10));
        }
        expect(handle.ink.document.strokes.length, 10);
        expect(await drafts.release(handle), isTrue);
        expect(handle.disposed, isTrue);
        expect(drafts.find(key), isNull);
      }
    }
    final h = drafts.open(
      const DraftKey('synthetic-owner', 'synthetic-patient', 'page-0'),
    );
    await h.ready;
    h.ink.restore(stressInk(page: 'page-0', strokes: 11));
    store.gate = Completer<void>();
    final saving = h.flush();
    final pausing = drafts.flushAll();
    store.fail = true;
    store.gate!.complete();
    expect(await saving, isFalse);
    expect(await pausing, isFalse);
    expect(h.dirty, isTrue);
    expect(h.ink.document.strokes.length, 11);
    expect(await drafts.flushAll(forLogout: true), isFalse);
    store.fail = false;
    expect(await h.flush(), isTrue);
    await drafts.release(h);
    final reopened = drafts.open(h.key);
    await reopened.ready;
    expect(reopened.ink.document.strokes.length, 11);
    // ignore: avoid_print
    print(
      'STRESS memory_store_300_open_close_ms=${watch.elapsedMilliseconds} drafts=${store.rows.length}',
    );
    await drafts.close();
  });

  test(
    '100 queued revisions survive manager reopen and bounded foreground drain',
    () async {
      final f = AuthFixture();
      final server = NotebookServer(f);
      await f.signIn();
      final key = DraftKey(f.cubit.draftOwner!, 'one', 'a');
      final h = f.cubit.drafts.open(key);
      await h.ready;
      h.ink.restore(stressInk(patient: 'one', page: 'a', strokes: 1));
      await h.flush();
      final bytes = encodeServerInk(h.ink.document);
      for (var i = 0; i < 100; i++) {
        await f.drafts.enqueue(
          key,
          NotebookDraft(
            patientId: 'one',
            pageId: 'a',
            expectedRowVersion: projection('one', 'a')['rowVersion'] as String,
            originDeviceId: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
            amendment: false,
            payload: bytes,
          ),
          i,
        );
      }
      await f.cubit.drafts.release(h);
      final queue = NotebookSyncQueue(
        f.cubit.drafts,
        NotebookApi(f.api),
        () => f.cubit.draftOwner,
      );
      final watch = Stopwatch()..start();
      await queue.drain();
      expect(server.mutations, 32);
      for (var i = 0; i < 3; i++) {
        await queue.drain();
      }
      expect(server.mutations, 100);
      expect(await f.drafts.queued(key.owner), isEmpty);
      // ignore: avoid_print
      print('STRESS fake_network_100_ack_ms=${watch.elapsedMilliseconds}');
      await queue.close();
      await f.cubit.close();
    },
  );

  test(
    'oversized document remains encrypted locally with a safe actionable issue',
    () async {
      final f = AuthFixture();
      NotebookServer(f);
      f.backend.patientSearch = (r) async =>
          page(f.backend, r, [patient('one')]);
      await f.signIn();
      final patients = PatientContextCubit(f.cubit, PatientSearch(f.api));
      final n = NotebookCubit(f.cubit, patients, NotebookApi(f.api));
      await patients.search('Patient');
      patients.select(patients.state.items.first);
      await settled(n);
      await n.open('a');
      final h = n.activeDraft!;
      h.ink.restore(
        stressInk(patient: 'one', page: 'a', strokes: 1, points: 20001),
      );
      await n.submit();
      expect(n.state.issue, NotebookIssue.documentRejected);
      expect(h.saved, isTrue);
      expect(h.ink.document.strokes.single.points.length, 20001);
      expect(await f.drafts.queued(h.key.owner), isEmpty);
      await n.close();
      await patients.close();
      await f.cubit.close();
    },
  );

  testWidgets(
    'debug diagnostics counts pressure and contacts without storing ink',
    (tester) async {
      final metrics = StylusMetrics();
      metrics.event(
        const PointerDownEvent(
          pointer: 1,
          kind: PointerDeviceKind.invertedStylus,
          pressure: .4,
          pressureMin: .1,
          pressureMax: .8,
        ),
      );
      metrics.event(const PointerDownEvent(pointer: 2));
      expect(metrics.suppressedTouches, 1);
      metrics.event(
        const PointerCancelEvent(
          pointer: 1,
          kind: PointerDeviceKind.invertedStylus,
        ),
      );
      expect(metrics.contact, 'cancel');
      expect(metrics.kind, 'invertedStylus');
      await tester.pumpWidget(const MaterialApp(home: StylusDiagnostics()));
      final gesture = await tester.startGesture(
        tester.getCenter(find.byKey(const Key('diagnostic-surface'))),
        kind: PointerDeviceKind.stylus,
      );
      await gesture.up();
      await tester.pump(const Duration(milliseconds: 250));
      expect(find.textContaining('pen: up'), findsOneWidget);
      await tester.pumpWidget(const SizedBox());
    },
  );
}
