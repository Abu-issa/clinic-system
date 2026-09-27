import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:doctor_tablet/app/doctor_tablet_app.dart';
import 'package:doctor_tablet/app/session/session_cubit.dart';
import 'package:doctor_tablet/features/notebook/data/local_ink_draft.dart';
import 'package:doctor_tablet/features/notebook/ink/ink_document.dart';
import 'package:doctor_tablet/features/notebook/presentation/ink_page.dart';
import 'package:doctor_tablet/features/notebook/state/local_drafts.dart';
import 'package:doctor_tablet/l10n/app_localizations.dart';
import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'auth_fixture.dart';
import 'draft_fixture.dart';

const binding = DraftKey('server/staff-a', 'patient-a', 'page-a');
void draw(DraftHandle h, {int pointer = 1}) {
  void send(PointerEvent event) => h.ink.handle(
    event,
    const Size(210, 297),
    patientId: h.key.patientId,
    pageId: h.key.pageId,
    enabled: h.canEdit,
  );
  send(
    PointerDownEvent(
      pointer: pointer,
      kind: PointerDeviceKind.stylus,
      position: const Offset(20, 30),
    ),
  );
  send(
    PointerMoveEvent(
      pointer: pointer,
      kind: PointerDeviceKind.stylus,
      position: const Offset(50, 60),
    ),
  );
  send(PointerUpEvent(pointer: pointer, kind: PointerDeviceKind.stylus));
}

LocalInkDraft sample([DraftKey key = binding]) => LocalInkDraft(
  key: key,
  updatedAt: DateTime.utc(2026, 9, 22),
  serverRevision: 3,
  serverRowVersion: 'version-3',
  document: InkDocument(
    patientId: key.patientId,
    pageId: key.pageId,
    strokes: [
      InkStroke(
        id: 12,
        color: 0xff1565c0,
        width: .7,
        points: const [InkPoint(x: 5, y: 6, timeMicros: 700, pressure: .5)],
      ),
    ],
  ),
);

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  test('local codec retains schema, strokes, metadata and exact binding', () {
    final row = encodeLocalDraft(sample());
    expect(row['formatVersion'], 1);
    expect(row['syncState'], 'localOnly');
    final restored = decodeLocalDraft((binding, row));
    expect(restored.document.strokes.single.points.single.pressure, .5);
    expect(restored.serverRevision, 3);
    expect(restored.serverRowVersion, 'version-3');
    expect(restored.updatedAt, DateTime.utc(2026, 9, 22));
  });
  for (final field in ['patientId', 'pageId', 'owner', 'formatVersion']) {
    test('$field metadata mismatch refuses restore', () {
      final row = encodeLocalDraft(sample());
      row[field] = 'wrong';
      expect(() => decodeLocalDraft((binding, row)), throwsFormatException);
    });
  }
  for (final field in ['patientId', 'pageId', 'formatVersion']) {
    test('$field payload mismatch refuses restore even with valid row', () {
      final row = encodeLocalDraft(sample());
      final payload =
          jsonDecode(row['payload']! as String) as Map<String, dynamic>;
      payload[field] = 'wrong';
      row['payload'] = jsonEncode(payload);
      expect(() => decodeLocalDraft((binding, row)), throwsFormatException);
    });
  }
  testWidgets(
    'debounce waits 1.5 seconds after last committed edit and reopen preserves strokes',
    (tester) async {
      final store = MemoryDraftStore();
      final drafts = LocalDrafts(store);
      final h = drafts.open(binding, revision: 4, rowVersion: 'rv4');
      await h.ready;
      expect(h.saved, isFalse);
      draw(h);
      await tester.pump(const Duration(seconds: 1));
      expect(store.writes, 0);
      draw(h);
      await tester.pump(const Duration(milliseconds: 1499));
      expect(store.writes, 0);
      await tester.pump(const Duration(milliseconds: 1));
      expect(store.writes, 1);
      expect(h.saved, isTrue);
      expect(h.dirty, isFalse);
      drafts.leave(h);
      await tester.pump();
      final reopened = drafts.open(binding);
      await reopened.ready;
      expect(reopened.ink.document.strokes.length, 2);
      expect(reopened.ink.canUndo, isFalse);
      expect(reopened.revision, 4);
      expect(reopened.rowVersion, 'rv4');
      await drafts.close();
    },
  );
  testWidgets(
    'hard checkpoint at 30 seconds during continuous committed edits',
    (tester) async {
      final store = MemoryDraftStore();
      final drafts = LocalDrafts(store);
      final h = drafts.open(binding);
      await h.ready;
      draw(h);
      for (var second = 1; second < 30; second++) {
        await tester.pump(const Duration(seconds: 1));
        draw(h);
      }
      expect(store.writes, 0);
      await tester.pump(const Duration(seconds: 1));
      expect(store.writes, 1);
      expect((await store.read(binding))!.document.strokes.length, 30);
      await drafts.close();
    },
  );
  testWidgets(
    'saving does not block ink, older completion cannot claim current edits saved',
    (tester) async {
      final store = MemoryDraftStore();
      final drafts = LocalDrafts(store);
      final h = drafts.open(binding);
      await h.ready;
      draw(h);
      store.gate = Completer<void>();
      final saving = h.flush();
      await tester.pump();
      expect(h.canEdit, isTrue);
      draw(h, pointer: 2);
      expect(h.ink.document.strokes.length, 2);
      store.gate!.complete();
      await saving;
      expect(h.saved, isFalse);
      expect(h.dirty, isTrue);
      await h.flush();
      expect(h.saved, isTrue);
      expect((await store.read(binding))!.document.strokes.length, 2);
      await drafts.close();
    },
  );
  for (final target in [
    const DraftKey('server/staff-a', 'patient-a', 'page-b'),
    const DraftKey('server/staff-a', 'patient-b', 'page-a'),
  ]) {
    testWidgets(
      '${target.patientId}/${target.pageId} switch flushes immediately and failures retain old draft',
      (tester) async {
        final store = MemoryDraftStore();
        final drafts = LocalDrafts(store);
        final h = drafts.open(binding);
        await h.ready;
        draw(h);
        store.fail = true;
        drafts.leave(h);
        await tester.pump();
        expect(h.dirty, isTrue);
        expect(h.saved, isFalse);
        store.fail = false;
        final next = drafts.open(target);
        await next.ready;
        expect(next.ink.document.strokes, isEmpty);
        final retained = drafts.open(binding);
        expect(retained, same(h));
        drafts.leave(retained);
        await tester.pump();
        expect(store.writes, 1);
        expect((await store.read(binding))!.document.strokes.length, 1);
        await drafts.close();
      },
    );
  }
  testWidgets(
    'restore failure is fail closed and does not overwrite the mismatched row',
    (tester) async {
      final store = MemoryDraftStore();
      final row = encodeLocalDraft(sample());
      row['patientId'] = 'wrong';
      store.rows[binding] = row;
      final drafts = LocalDrafts(store);
      final h = drafts.open(binding);
      await h.ready;
      expect(h.failedRestore, isTrue);
      expect(h.canEdit, isFalse);
      draw(h);
      expect(h.ink.document.strokes, isEmpty);
      expect(await h.flush(), isFalse);
      expect(store.writes, 0);
      row['patientId'] = binding.patientId;
      await h.retryRestore();
      expect(h.ink.document.strokes.single.id, 12);
      draw(h);
      expect(h.ink.document.strokes.last.id, 13);
      await drafts.close();
    },
  );
  testWidgets(
    'app background flushes a partial pen stroke through the root lifecycle observer',
    (tester) async {
      final f = AuthFixture();
      final signIn = f.signIn();
      await tester.pumpAndSettle();
      await signIn;
      await tester.pumpWidget(DoctorTabletApp(sessionCubit: f.cubit));
      await tester.pumpAndSettle();
      final h = f.cubit.drafts.open(binding);
      await h.ready;
      h.ink.handle(
        const PointerDownEvent(
          pointer: 7,
          kind: PointerDeviceKind.stylus,
          position: Offset(20, 20),
        ),
        const Size(210, 297),
        patientId: binding.patientId,
        pageId: binding.pageId,
        enabled: true,
      );
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.inactive);
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.hidden);
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.paused);
      await tester.pump();
      expect(f.drafts.writes, 1);
      expect((await f.drafts.read(binding))!.document.strokes.length, 1);
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.hidden);
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.inactive);
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.resumed);
      await tester.pumpWidget(const SizedBox());
      await tester.pump();
    },
  );
  test('logout refuses save failure, preserves memory, then preserves encrypted-store record on success', () async {
    final f = AuthFixture();
    await f.signIn();
    final h = f.cubit.drafts.open(binding);
    await h.ready;
    draw(h);
    f.drafts.fail = true;
    expect(await f.cubit.logout(), isFalse);
    expect(f.cubit.state, isA<SessionAuthenticated>());
    expect(f.backend.logouts, 0);
    expect(h.ink.document.strokes.length, 1);
    expect(h.saved, isFalse);
    f.drafts.fail = false;
    expect(await f.cubit.logout(), isTrue);
    expect(f.cubit.state, isA<SessionUnauthenticated>());
    expect(f.drafts.rows.containsKey(binding), isTrue);
    expect((await f.drafts.read(binding))!.document.strokes.length, 1);
    await f.cubit.close();
  });
  for (final lang in ['en', 'ar']) {
    testWidgets('$lang honest save labels, failure, page switch and reopen', (
      tester,
    ) async {
      final store = MemoryDraftStore();
      final drafts = LocalDrafts(store);
      Future<void> mount(String page) async {
        await tester.pumpWidget(
          MaterialApp(
            locale: Locale(lang),
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            home: Scaffold(
              body: SingleChildScrollView(
                child: SizedBox(
                  width: 420,
                  child: InkPage(
                    drafts: drafts,
                    owner: binding.owner,
                    patientId: binding.patientId,
                    pageId: page,
                    enabled: true,
                    isCurrent: () => true,
                  ),
                ),
              ),
            ),
          ),
        );
        await tester.pump();
      }

      await mount(binding.pageId);
      final strings = AppLocalizations.of(tester.element(find.byType(InkPage)));
      expect(find.text(strings.inkLocalChanges), findsOneWidget);
      final h = drafts.open(binding);
      await h.ready;
      draw(h);
      store.fail = true;
      await tester.pump(const Duration(milliseconds: 1500));
      expect(find.text(strings.inkLocalSaved), findsNothing);
      expect(find.text(strings.inkSaveFailed), findsOneWidget);
      store.fail = false;
      await mount('other');
      await tester.pump();
      expect(store.writes, 1); // Switch flush; no debounce wait.
      await mount(binding.pageId);
      expect(find.text(strings.inkLocalSaved), findsOneWidget);
      final painter =
          tester
                  .widget<CustomPaint>(find.byKey(const Key('ink-committed')))
                  .painter!
              as InkPainter;
      expect(painter.strokes.length, 1);
      expect(find.text('SERVER SYNCED'), findsNothing);
      await tester.pumpWidget(const SizedBox());
      await tester.pump();
      await drafts.close();
    });
  }
  test(
    'production draft code has no plaintext file or preferences write path',
    () {
      final source = Directory('lib/features/notebook')
          .listSync(recursive: true)
          .whereType<File>()
          .where((f) => f.path.endsWith('.dart'))
          .map((f) => f.readAsStringSync())
          .join('\n');
      expect(source, isNot(contains('SharedPreferences')));
      expect(source, isNot(contains('writeAsString')));
      expect(source, isNot(contains('writeAsBytes')));
      expect(source, contains('password: key'));
      expect(source, contains('PRAGMA cipher_version'));
    },
  );
}
