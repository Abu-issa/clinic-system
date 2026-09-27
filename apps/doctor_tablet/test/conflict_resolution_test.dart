import 'dart:async';

import 'package:dio/dio.dart';
import 'package:doctor_tablet/app/doctor_tablet_app.dart';
import 'package:doctor_tablet/features/notebook/data/local_ink_draft.dart';
import 'package:doctor_tablet/features/notebook/data/notebook_api.dart';
import 'package:doctor_tablet/features/notebook/data/server_ink_codec.dart';
import 'package:doctor_tablet/features/notebook/ink/ink_document.dart';
import 'package:doctor_tablet/features/notebook/state/local_drafts.dart';
import 'package:doctor_tablet/features/notebook/state/notebook_cubit.dart';
import 'package:doctor_tablet/features/patients/data/patient_search.dart';
import 'package:doctor_tablet/features/patients/state/patient_context_cubit.dart';
import 'package:doctor_tablet/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';

import 'auth_fixture.dart';
import 'ink_sync_test.dart' show until;
import 'local_drafts_test.dart' show draw;
import 'notebook_test.dart' show NotebookServer, projection, settled;
import 'patient_context_test.dart' show patient, page;

class ConflictFixture {
  final f = AuthFixture();
  late NotebookServer server;
  late PatientContextCubit patients;
  late NotebookCubit notebook;
  DraftHandle get draft => notebook.activeDraft!;
  InkDocument get remote => InkDocument(
    patientId: 'one',
    pageId: 'a',
    strokes: [
      InkStroke(
        id: 900,
        color: 0xff000000,
        width: .7,
        points: const [InkPoint(x: 100, y: 120, timeMicros: 0)],
      ),
    ],
  );
  Future<void> init({bool finalized = false}) async {
    server = NotebookServer(f);
    if (finalized) {
      server.pages['a'] = projection('one', 'a', revision: 1, finalized: true);
    }
    f.backend.patientSearch = (r) async =>
        page(f.backend, r, [patient('one'), patient('two')]);
    f.backend.notebook = (r) async {
      if (r.path.endsWith('/payload')) {
        return ResponseBody.fromBytes(
          encodeServerInk(remote),
          200,
          headers: {
            Headers.contentTypeHeader: ['application/msgpack'],
          },
        );
      }
      return server.handle(r);
    };
    await f.signIn();
    patients = PatientContextCubit(f.cubit, PatientSearch(f.api));
    notebook = NotebookCubit(f.cubit, patients, NotebookApi(f.api));
    await patients.search('Patient');
    patients.select(patients.state.items.first);
    await settled(notebook);
    await notebook.open('a');
    if (finalized) notebook.beginAmendment();
    draw(draft);
    await draft.flush();
    server.stale = true;
    await notebook.submit(amendment: finalized);
    expect(draft.syncState, 'conflict');
    server.stale = false;
    server.pages['a'] = projection(
      'one',
      'a',
      revision: 7,
      finalized: finalized,
    );
  }

  Future<void> close() async {
    await notebook.close();
    await patients.close();
    await f.cubit.close();
  }
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  group('explicit resolution', () {
    late ConflictFixture x;
    setUp(() async {
      x = ConflictFixture();
      await x.init();
    });
    tearDown(() => x.close());

    test(
      'Cancel and unconfirmed discard leave queue and draft unchanged',
      () async {
        final local = Map.of(x.f.drafts.rows[x.draft.key]!);
        final ids = x.f.drafts.queueRows.keys.toList();
        final requests = x.server.requests.length;
        await x.notebook.resolveConflict(ConflictChoice.later);
        await x.notebook.resolveConflict(ConflictChoice.discard);
        expect(x.f.drafts.rows[x.draft.key], local);
        expect(x.f.drafts.queueRows.keys, ids);
        expect(x.server.requests.length, requests);
        expect(x.notebook.canFinalize, isFalse);
      },
    );

    test(
      'failed server refresh keeps every local stroke and queued operation',
      () async {
        final doc = x.draft.ink.document, base = x.draft.rowVersion;
        final ids = x.f.drafts.queueRows.keys.toList();
        x.server.fail = true;
        expect(
          await x.notebook.resolveConflict(
            ConflictChoice.discard,
            confirmed: true,
          ),
          isFalse,
        );
        expect(x.draft.ink.document, same(doc));
        expect(x.draft.rowVersion, base);
        expect(x.f.drafts.queueRows.keys, ids);
        expect(x.draft.syncState, 'conflict');
        expect(x.notebook.state.issue, NotebookIssue.resolutionFailed);
      },
    );

    test('successful discard restores server ink and clears only the selected page', () async {
      final other = x.f.cubit.drafts.open(
        DraftKey(x.draft.key.owner, 'two', 'b'),
      );
      await other.ready;
      draw(other);
      final otherPage = NotebookPage.fromJson(
        projection('two', 'b'),
        'two',
        'b',
      );
      await x.notebook.queue!.enqueue(
        other,
        otherPage,
        x.notebook.originDeviceId,
      );
      final unrelated = (await x.f.drafts.queued(other.key.owner)).last;
      final local = x.draft.ink.document;
      expect(
        await x.notebook.resolveConflict(
          ConflictChoice.discard,
          confirmed: true,
        ),
        isTrue,
      );
      expect(x.draft.ink.document, isNot(same(local)));
      expect(x.draft.ink.document.strokes.single.id, 900);
      expect(x.draft.revision, 7);
      expect(x.draft.rowVersion, x.server.pages['a']!['rowVersion']);
      expect(x.draft.ink.canUndo, isFalse);
      final remaining = await x.f.drafts.queued(other.key.owner);
      expect(remaining.single.id, unrelated.id);
      expect(remaining.single.sequence, unrelated.sequence);
      expect(remaining.single.envelope.bytes, unrelated.envelope.bytes);
      expect(x.server.mutations, 0);
      expect(x.notebook.state.issue, NotebookIssue.resolved);
    });

    test(
      'payload binding or concurrent server change makes discard fail safely',
      () async {
        final before = x.draft.ink.document;
        final delegate = x.f.backend.notebook!;
        x.f.backend.notebook = (r) async {
          if (r.path.endsWith('/payload')) {
            return ResponseBody.fromBytes(
              encodeServerInk(InkDocument(patientId: 'two', pageId: 'a')),
              200,
            );
          }
          return delegate(r);
        };
        expect(
          await x.notebook.resolveConflict(
            ConflictChoice.discard,
            confirmed: true,
          ),
          isFalse,
        );
        expect(x.draft.ink.document, same(before));
        expect(x.f.drafts.queueRows.length, 1);
        x.f.backend.notebook = (r) async {
          final result = await delegate(r);
          if (r.path.endsWith('/payload')) {
            x.server.pages['a'] = projection('one', 'a', revision: 8);
          }
          return result;
        };
        expect(
          await x.notebook.resolveConflict(
            ConflictChoice.discard,
            confirmed: true,
          ),
          isFalse,
        );
        expect(x.draft.syncState, 'conflict');
      },
    );

    test('explicit rebase uses latest RowVersion, NEW draft ID and identical ink bytes', () async {
      final old = (await x.f.drafts.queued(x.draft.key.owner)).single;
      final bytes = encodeServerInk(x.draft.ink.document);
      final latestVersion = x.server.pages['a']!['rowVersion'];
      final delegate = x.f.backend.notebook!;
      x.f.backend.notebook = (r) async {
        if (r.data is FormData) expect(x.draft.resolution!.bytes, bytes);
        return delegate(r);
      };
      expect(
        await x.notebook.resolveConflict(ConflictChoice.saveAsNew),
        isTrue,
      );
      final sent =
          x.server.requests.lastWhere((r) => r.data is FormData).data
              as FormData;
      final fields = Map.fromEntries(sent.fields);
      expect(fields['expectedRowVersion'], latestVersion);
      expect(fields['clientDraftId'], isNot(old.id));
      expect(encodeServerInk(x.draft.ink.document), bytes);
      expect(x.draft.syncState, 'serverSynced');
      expect(x.draft.revision, 8);
      expect(x.f.drafts.queueRows, isEmpty);
      expect(x.server.mutations, 1);
      expect(x.notebook.canFinalize, isTrue);
    });

    test(
      'failed rebase retains conflict, original base and entire queue',
      () async {
        final doc = x.draft.ink.document, base = x.draft.rowVersion;
        final old = (await x.f.drafts.queued(x.draft.key.owner)).single;
        x.server.stale = true;
        expect(
          await x.notebook.resolveConflict(ConflictChoice.saveAsNew),
          isFalse,
        );
        expect(x.draft.syncState, 'conflict');
        expect(x.draft.ink.document, same(doc));
        expect(x.draft.rowVersion, base);
        expect((await x.f.drafts.queued(x.draft.key.owner)).single.id, old.id);
        expect(x.notebook.canFinalize, isFalse);
      },
    );

    test('encrypted transaction failure after ACK keeps conflict; retry uses same identity', () async {
      x.f.drafts.failResolution = true;
      final doc = x.draft.ink.document;
      expect(
        await x.notebook.resolveConflict(ConflictChoice.saveAsNew),
        isFalse,
      );
      expect(x.server.mutations, 1);
      expect(x.draft.syncState, 'conflict');
      expect(x.draft.ink.document, same(doc));
      expect(x.f.drafts.queueRows.length, 1);
      final pending = x.draft.resolution!;
      expect(
        (await x.f.drafts.read(x.draft.key))!.resolution!.clientDraftId,
        pending.clientDraftId,
      );
      x.f.drafts.failResolution = false;
      expect(
        await x.notebook.resolveConflict(ConflictChoice.saveAsNew),
        isTrue,
      );
      expect(x.server.mutations, 1);
      expect(x.f.drafts.queueRows, isEmpty);
    });

    test(
      'uncertain resolution survives restart and never drains automatically',
      () async {
        x.server.loseReply = true;
        expect(
          await x.notebook.resolveConflict(ConflictChoice.saveAsNew),
          isFalse,
        );
        final old = x.draft, pending = x.draft.resolution!;
        x.f.cubit.drafts.leave(old);
        await until(() => old.disposed);
        final restored = x.f.cubit.drafts.open(old.key);
        await restored.ready;
        expect(restored.syncState, 'conflict');
        expect(restored.resolution!.clientDraftId, pending.clientDraftId);
        final count = x.server.requests.length;
        await x.notebook.queue!.drain(force: true);
        expect(x.server.requests.length, count);
        expect(
          await x.notebook.resolveConflict(ConflictChoice.saveAsNew),
          isTrue,
        );
        expect(x.server.mutations, 1);
      },
    );

    test(
      'other pages continue draining while this page remains conflicted',
      () async {
        x.server.pages['b'] = projection('two', 'b');
        final other = x.f.cubit.drafts.open(
          DraftKey(x.draft.key.owner, 'two', 'b'),
        );
        await other.ready;
        draw(other);
        await x.notebook.queue!.enqueue(
          other,
          NotebookPage.fromJson(x.server.pages['b']!, 'two', 'b'),
          x.notebook.originDeviceId,
        );
        await x.notebook.queue!.drain();
        expect(other.serverSynced, isTrue);
        expect(x.draft.syncState, 'conflict');
        expect(x.f.drafts.queueRows.length, 1);
      },
    );

    test(
      'another authenticated doctor cannot resolve the prior owner queue',
      () async {
        final oldKey = x.draft.key;
        final oldIds = x.f.drafts.queueRows.keys.toList();
        await x.f.cubit.logout();
        x.f.backend.staffId = 'staff-2';
        await x.f.signIn();
        await Future<void>.delayed(Duration.zero);
        await x.patients.search('Patient');
        x.patients.select(x.patients.state.items.first);
        await settled(x.notebook);
        await x.notebook.open('a');
        expect(x.draft.key.owner, isNot(oldKey.owner));
        expect(
          await x.notebook.resolveConflict(
            ConflictChoice.discard,
            confirmed: true,
          ),
          isFalse,
        );
        expect(
          await x.notebook.resolveConflict(ConflictChoice.saveAsNew),
          isFalse,
        );
        expect(x.f.drafts.queueRows.keys, oldIds);
        expect((await x.f.drafts.read(oldKey))!.syncState, 'conflict');
        expect(x.server.mutations, 0);
      },
    );

    test('patient change after ACK rolls back local resolution and retains retry identity', () async {
      final old = x.draft;
      x.f.drafts.resolutionGate = Completer<void>();
      final attempt = x.notebook.resolveConflict(ConflictChoice.saveAsNew);
      await until(() => x.server.mutations == 1);
      x.patients.select(x.patients.state.items.last);
      await Future<void>.delayed(Duration.zero);
      x.f.drafts.resolutionGate!.complete();
      expect(await attempt, isFalse);
      expect(old.syncState, 'conflict');
      expect(old.resolution, isNotNull);
      expect(x.f.drafts.queueRows.length, 1);
      expect(x.notebook.state.selected, isNull);
    });

    for (final account in [false, true]) {
      test(
        '${account ? 'account' : 'patient'} switch cannot commit a late resolution',
        () async {
          final old = x.draft;
          x.server.gate = Completer<void>();
          final before = x.server.requests.length;
          final attempt = x.notebook.resolveConflict(
            ConflictChoice.discard,
            confirmed: true,
          );
          await until(() => x.server.requests.length > before);
          if (account) {
            await x.f.cubit.logout();
          } else {
            x.patients.select(x.patients.state.items.last);
          }
          await Future<void>.delayed(Duration.zero);
          x.server.gate!.complete();
          expect(await attempt, isFalse);
          expect(old.syncState, 'conflict');
          expect(x.f.drafts.queueRows.length, 1);
          expect(x.notebook.state.selected, isNull);
        },
      );
    }
  });

  test('finalized amendment conflict rebases via amendment endpoint', () async {
    final x = ConflictFixture();
    await x.init(finalized: true);
    try {
      expect(
        (await x.f.drafts.queued(x.draft.key.owner)).single.envelope.amendment,
        isTrue,
      );
      expect(
        await x.notebook.resolveConflict(ConflictChoice.saveAsNew),
        isTrue,
      );
      expect(
        x.server.requests.lastWhere((r) => r.data is FormData).path,
        endsWith('/amendments'),
      );
      expect(x.notebook.state.selected!.finalized, isTrue);
      expect(x.notebook.canRevise, isFalse);
      expect(x.notebook.canDraw, isFalse);
    } finally {
      await x.close();
    }
  });

  for (final language in ['en', 'ar']) {
    testWidgets(
      '$language conflict UI, explicit confirmation and persistent patient header',
      (tester) async {
        final f = AuthFixture();
        // Use the same transport fixture as the widget session.
        final backend = NotebookServer(f);
        f.backend.patientSearch = (r) async =>
            page(f.backend, r, [patient('one')]);

        final signingIn = f.signIn();
        await tester.pumpAndSettle();
        await signingIn;

        await tester.pumpWidget(
          DoctorTabletApp(
            sessionCubit: f.cubit,
            initialLocale: Locale(language),
          ),
        );
        await tester.pumpAndSettle();
        final context = tester.element(
          find.byKey(const Key('patient-search-term')),
        );
        final patients = context.read<PatientContextCubit>();
        final searching = patients.search('Patient');
        await tester.pumpAndSettle();
        await searching;

        patients.select(patients.state.items.first);
        await tester.pumpAndSettle();
        final n = context.read<NotebookCubit>();
        final opening = n.open('a');
        await tester.pumpAndSettle();
        await opening;

        draw(n.activeDraft!);
        backend.stale = true;

        var submitted = false;
        final submitting = n.submit().whenComplete(() => submitted = true);
        for (var i = 0; i < 200 && !submitted; i++) {
          await tester.pump(const Duration(milliseconds: 20));
          await tester.runAsync(
            () => Future<void>.delayed(const Duration(milliseconds: 5)),
          );
        }
        expect(submitted, isTrue);
        await submitting;

        await tester.pumpAndSettle();
        final s = AppLocalizations.of(context);
        expect(find.byKey(const Key('conflict-panel')), findsOneWidget);
        expect(find.byKey(const Key('patient-header')), findsOneWidget);
        expect(
          s.conflictDetected,
          language == 'en' ? 'Conflict detected' : 'تم اكتشاف تعارض',
        );
        expect(find.text(s.conflictSaveNew), findsOneWidget);
        expect(find.text(s.conflictDiscard), findsOneWidget);
        expect(find.text(s.conflictLater), findsOneWidget);
        expect(
          Directionality.of(
            tester.element(find.byKey(const Key('conflict-panel'))),
          ),
          language == 'ar' ? TextDirection.rtl : TextDirection.ltr,
        );
        final requests = backend.requests.length;
        await tester.ensureVisible(find.byKey(const Key('conflict-discard')));
        await tester.pumpAndSettle();
        expect(n.state.busy, isFalse);
        expect(
          tester
              .widget<OutlinedButton>(find.byKey(const Key('conflict-discard')))
              .onPressed,
          isNotNull,
        );

        await tester.tap(find.byKey(const Key('conflict-discard')));
        await tester.pumpAndSettle();
        expect(find.text(s.conflictConfirmDiscard), findsOneWidget);
        expect(backend.requests.length, requests);
        expect(f.drafts.queueRows.length, 1);
        await tester.ensureVisible(find.byKey(const Key('conflict-later')));
        await tester.pumpAndSettle();
        await tester.tap(find.byKey(const Key('conflict-later')));
        await tester.pumpAndSettle();
        expect(find.byKey(const Key('conflict-panel')), findsNothing);
        expect(f.drafts.queueRows.length, 1);
        expect(n.activeDraft!.syncState, 'conflict');
        expect(backend.requests.length, requests);
        expect(tester.takeException(), isNull);
        await tester.pumpWidget(const SizedBox.shrink());
      },
    );
  }
}
