import 'dart:async';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:doctor_tablet/features/notebook/data/notebook_api.dart';
import 'package:doctor_tablet/features/notebook/data/local_ink_draft.dart';
import 'package:doctor_tablet/features/notebook/data/server_ink_codec.dart';
import 'package:doctor_tablet/features/notebook/ink/ink_document.dart';
import 'package:doctor_tablet/features/notebook/presentation/ink_page.dart';
import 'package:doctor_tablet/features/notebook/state/notebook_cubit.dart';
import 'package:doctor_tablet/features/notebook/state/local_drafts.dart';
import 'package:doctor_tablet/features/patients/data/patient_search.dart';
import 'package:doctor_tablet/features/patients/state/patient_context_cubit.dart';
import 'package:doctor_tablet/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'auth_fixture.dart';
import 'draft_fixture.dart';
import 'notebook_test.dart' show NotebookServer, settled;
import 'patient_context_test.dart' show patient, page;
import 'local_drafts_test.dart' show draw;

Future<void> until(bool Function() predicate) async {
  for (var i = 0; i < 1000 && !predicate(); i++) {
    await Future<void>.delayed(const Duration(milliseconds: 2));
  }
  expect(predicate(), isTrue);
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  test('MessagePack deterministic round trip preserves logical coordinates and relative timing', () {
    final doc = InkDocument(
      patientId: 'patient',
      pageId: 'page',
      strokes: [
        InkStroke(
          id: 9,
          color: 0xff1565c0,
          width: .7,
          points: const [
            InkPoint(x: 10, y: 20, pressure: .2, timeMicros: 50000),
            InkPoint(x: 210, y: 297, pressure: .8, timeMicros: 70000),
          ],
        ),
      ],
    );
    final bytes = encodeServerInk(doc);
    expect(encodeServerInk(doc), bytes);
    final restored = decodeServerInk(bytes, 'patient', 'page');
    expect(restored.strokes.single.points.last.x, 210);
    expect(restored.strokes.single.points.last.y, 297);
    expect(restored.strokes.single.points.last.timeMicros, 20000);
    expect(restored.strokes.single.points.first.timeMicros, 0);
    expect(restored.strokes.single.id, 9);
    expect(encodeServerInk(restored), bytes);
    expect(
      () => decodeServerInk(bytes, 'other', 'page'),
      throwsFormatException,
    );
    expect(
      () => decodeServerInk(bytes, 'patient', 'other'),
      throwsFormatException,
    );
    expect(
      () =>
          decodeServerInk(Uint8List.fromList([...bytes, 0]), 'patient', 'page'),
      throwsFormatException,
    );
    expect(
      () => decodeServerInk(
        Uint8List.fromList([0xdd, 255, 255, 255, 255]),
        'patient',
        'page',
      ),
      throwsFormatException,
    );
  });

  group('real snapshot synchronization', () {
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
      await notebook.open('a');
      draw(notebook.activeDraft!);
      await notebook.activeDraft!.flush();
    });
    tearDown(() async {
      await notebook.close();
      await patients.close();
      await f.cubit.close();
    });

    test(
      'uncertain envelope survives reopen and retries the same revision',
      () async {
        server.loseReply = true;
        await notebook.submit();
        final old = notebook.activeDraft!;
        final envelope = (await f.drafts.queued(old.key.owner)).single.envelope;
        f.cubit.drafts.leave(old);
        await until(() => old.disposed);
        final reopened = f.cubit.drafts.open(old.key);
        await reopened.ready;
        expect(
          (await f.drafts.queued(reopened.key.owner))
              .single
              .envelope
              .clientDraftId,
          envelope.clientDraftId,
        );
        expect(
          (await f.drafts.queued(reopened.key.owner))
              .single
              .envelope
              .originDeviceId,
          envelope.originDeviceId,
        );
        expect(
          (await f.drafts.queued(reopened.key.owner)).single.envelope.bytes,
          envelope.bytes,
        );
        await notebook.retry();
        expect(server.mutations, 1);
        expect(reopened.serverSynced, isTrue);
      },
    );

    test('failed local persistence after ACK retains envelope without synced claim', () async {
      final delegate = server.handle;
      f.backend.notebook = (r) async {
        final response = await delegate(r);
        if (r.data is FormData) f.drafts.fail = true;
        return response;
      };
      await notebook.submit();
      final h = notebook.activeDraft!;
      expect(server.mutations, 1);
      expect(h.serverSynced, isFalse);
      expect(h.saved, isFalse);
      expect(f.drafts.queueRows, hasLength(1));
      expect(h.revision, 0);
      f.drafts.fail = false;
      f.backend.notebook = delegate;
      await notebook.retry();
      expect(server.mutations, 1);
      expect(h.serverSynced, isTrue);
    });

    test(
      'finalization conflict preserves base and remains blocked after refresh',
      () async {
        await notebook.submit();
        final h = notebook.activeDraft!;
        final base = h.rowVersion;
        server.stale = true;
        await notebook.finalize();
        expect(h.syncState, 'conflict');
        expect(h.rowVersion, base);
        server.stale = false;
        await notebook.refresh();
        expect(notebook.canFinalize, isFalse);
        expect((await f.drafts.read(h.key))!.syncState, 'conflict');
      },
    );

    test('LOCAL SAVED -> SYNCING -> SERVER SYNCED only after ACK/detail and encrypted metadata', () async {
      final h = notebook.activeDraft!;
      expect(h.saved, isTrue);
      expect(h.serverSynced, isFalse);
      server.gate = Completer<void>();
      final sync = notebook.submit();
      await until(() => server.requests.any((r) => r.data is FormData));
      expect(h.syncing, isTrue);
      expect(h.serverSynced, isFalse);
      expect(f.drafts.queueRows, hasLength(1));
      final wire = decodeServerInk(
        Uint8List.fromList(
          (await f.drafts.queued(h.key.owner)).single.envelope.bytes,
        ),
        'one',
        'a',
      );
      expect(wire.strokes.single.points.last.x, 50);
      server.gate!.complete();
      await sync;
      expect(h.serverSynced, isTrue);
      expect(h.revision, 1);
      expect(h.rowVersion, notebook.state.selected!.rowVersion);
      final persisted = await f.drafts.read(h.key);
      expect(persisted!.syncState, 'serverSynced');
      expect(persisted.submission, isNull);
    });
    test('offline keeps encrypted ink and exact retry envelope; successful retry does not duplicate', () async {
      final h = notebook.activeDraft!,
          original = notebook.activeDraft!.ink.document;
      server.loseReply = true;
      await notebook.submit();
      expect(h.syncState, 'offline');
      expect(h.saved, isTrue);
      expect(h.serverSynced, isFalse);
      final pending = (await f.drafts.queued(h.key.owner)).single.envelope;
      expect(
        (await f.drafts.queued(h.key.owner)).single.envelope.bytes,
        pending.bytes,
      );
      expect(
        (await f.drafts.queued(h.key.owner)).single.envelope.clientDraftId,
        pending.clientDraftId,
      );
      expect(
        (await f.drafts.queued(h.key.owner)).single.envelope.originDeviceId,
        pending.originDeviceId,
      );
      expect(h.ink.document, same(original));
      await notebook.retry();
      expect(server.mutations, 1);
      expect(h.serverSynced, isTrue);
    });
    test(
      '401 replay retains entire multipart body and exact vector payload',
      () async {
        f.backend.expiredAccess = true;
        await notebook.submit();
        expect(f.backend.refreshes, 1);
        expect(f.backend.multipartBodies.length, 2);
        expect(f.backend.multipartBodies[0], f.backend.multipartBodies[1]);
        expect(server.mutations, 1);
        expect(notebook.activeDraft!.serverSynced, isTrue);
      },
    );
    test('409 preserves payload/base and conflict survives explicit refresh and reopen', () async {
      final h = notebook.activeDraft!, base = notebook.activeDraft!.rowVersion;
      final original = h.ink.document;
      server.stale = true;
      await notebook.submit();
      final envelope = (await f.drafts.queued(h.key.owner)).single.envelope;
      expect(h.syncState, 'conflict');
      expect(h.rowVersion, base);
      expect(h.ink.document, same(original));
      server.stale = false;
      await notebook.refresh();
      expect(notebook.canRevise, isFalse);
      final mutations = server.mutations;
      await notebook.retry();
      await notebook.submit();
      expect(server.mutations, mutations);
      final saved = await f.drafts.read(h.key);
      expect(saved!.syncState, 'conflict');
      expect(
        (await f.drafts.queued(h.key.owner)).single.envelope.bytes,
        envelope.bytes,
      );
      f.cubit.drafts.leave(h);
      await Future<void>.delayed(Duration.zero);
      final reopened = f.cubit.drafts.open(h.key);
      await reopened.ready;
      expect(reopened.syncState, 'conflict');
      expect(
        (await f.drafts.queued(reopened.key.owner)).single.envelope.bytes,
        envelope.bytes,
      );
    });
    test('finalization is blocked with unsynced ink, then allowed after sync; amendment uses real ink', () async {
      await notebook.finalize();
      expect(notebook.state.selected!.finalized, isFalse);
      await notebook.submit();
      expect(notebook.canFinalize, isTrue);
      draw(notebook.activeDraft!);
      await notebook.finalize();
      expect(notebook.state.selected!.finalized, isFalse);
      await notebook.submit();
      await notebook.finalize();
      expect(notebook.state.selected!.finalized, isTrue);
      expect(notebook.canRevise, isFalse);
      expect(notebook.canDraw, isFalse);
      notebook.beginAmendment();
      expect(notebook.canDraw, isTrue);
      draw(notebook.activeDraft!);
      await notebook.submit(amendment: true);
      expect(notebook.state.selected!.finalized, isTrue);
      expect(notebook.activeDraft!.serverSynced, isTrue);
      expect(
        server.requests.any((r) => r.path.endsWith('/amendments')),
        isTrue,
      );
    });
    test('edits during upload remain unsynced and locally saved after old snapshot ACK', () async {
      final h = notebook.activeDraft!;
      server.gate = Completer<void>();
      final sync = notebook.submit();
      await until(() => server.requests.any((r) => r.data is FormData));
      draw(h, pointer: 2);
      server.gate!.complete();
      await sync;
      expect(h.ink.document.strokes.length, 2);
      expect(h.serverSynced, isFalse);
      expect(h.revision, 1);
      expect(h.saved, isTrue);
      expect(h.submission, isNull);
      await notebook.submit();
      expect(h.serverSynced, isTrue);
      expect(h.revision, 2);
    });
    test(
      'patient switch keeps background ACK confined to the queued patient',
      () async {
        final h = notebook.activeDraft!;
        server.gate = Completer<void>();
        final sync = notebook.submit();
        await until(() => server.requests.any((r) => r.data is FormData));
        patients.select(patients.state.items.last);
        await Future<void>.delayed(Duration.zero);
        server.gate!.complete();
        await sync;
        await settled(notebook);
        expect(notebook.state.patientId, 'two');
        expect(notebook.state.selected, isNull);
        expect(h.serverSynced, isTrue);
        expect(h.ink.document.patientId, 'one');
      },
    );
    test(
      'ACK without matching fresh detail does not claim synchronization',
      () async {
        final delegate = server.handle;
        f.backend.notebook = (r) async {
          final response = await delegate(r);
          if (r.data is FormData) {
            server.pages['a']!['currentRevisionNumber'] = 99;
          }
          return response;
        };
        await notebook.submit();
        expect(notebook.activeDraft!.syncState, 'conflict');
        expect(notebook.activeDraft!.serverSynced, isFalse);
        expect(notebook.activeDraft!.revision, 0);
      },
    );
  });

  for (final language in ['en', 'ar']) {
    testWidgets('$language sync-state labels and direction', (tester) async {
      await tester.pumpWidget(
        MaterialApp(
          locale: Locale(language),
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: const Scaffold(body: Text('labels')),
        ),
      );
      final context = tester.element(find.text('labels'));
      final s = AppLocalizations.of(context);
      expect(
        Directionality.of(context),
        language == 'ar' ? TextDirection.rtl : TextDirection.ltr,
      );
      expect(s.inkSyncing, language == 'en' ? 'SYNCING' : 'جارٍ المزامنة');
      expect(
        s.inkServerSynced,
        language == 'en' ? 'SERVER SYNCED' : 'تمت المزامنة مع الخادم',
      );
      expect(s.inkConflict, language == 'en' ? 'CONFLICT' : 'تعارض');
      expect(s.inkQueued, language == 'en' ? 'QUEUED' : 'في قائمة المزامنة');
      expect(
        s.inkSyncFailed,
        language == 'en' ? 'SYNC FAILED' : 'فشلت المزامنة',
      );
      expect(
        s.inkOffline,
        language == 'en'
            ? 'OFFLINE — SAVED LOCALLY'
            : 'غير متصل — محفوظ محلياً',
      );
      // Verify the actual status selector uses the same localized labels.
      final drafts = LocalDrafts(MemoryDraftStore());
      final h = drafts.open(const DraftKey('owner', 'p', 'a'));
      await h.ready;
      h.syncing = true;
      expect(inkSaveLabel(s, h, false), s.inkSyncing);
      h.syncing = false;
      h.syncState = 'conflict';
      expect(inkSaveLabel(s, h, false), s.inkConflict);
      h.syncState = 'offline';
      h.saved = true;
      expect(inkSaveLabel(s, h, false), s.inkOffline);
      h.syncState = 'serverSynced';
      expect(inkSaveLabel(s, h, false), s.inkServerSynced);
      h.syncState = 'queued';
      expect(inkSaveLabel(s, h, false), s.inkQueued);
      h.syncState = 'syncFailed';
      expect(inkSaveLabel(s, h, false), s.inkSyncFailed);
      h.syncState = 'localOnly';
      expect(inkSaveLabel(s, h, true), s.inkLocalChanges);
      await drafts.close();
    });
  }
}
