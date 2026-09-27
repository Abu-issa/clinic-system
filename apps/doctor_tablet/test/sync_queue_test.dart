import 'dart:async';

import 'package:dio/dio.dart';
import 'package:doctor_tablet/app/session/session_cubit.dart';
import 'package:doctor_tablet/features/notebook/data/local_ink_draft.dart';
import 'package:doctor_tablet/features/notebook/data/notebook_api.dart';
import 'package:doctor_tablet/features/notebook/state/local_drafts.dart';
import 'package:doctor_tablet/features/notebook/state/notebook_sync_queue.dart';
import 'package:flutter_test/flutter_test.dart';

import 'auth_fixture.dart';
import 'ink_sync_test.dart' show until;
import 'local_drafts_test.dart' show draw;
import 'notebook_test.dart' show NotebookServer, projection;

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  late AuthFixture f;
  late NotebookServer server;
  late NotebookSyncQueue queue;
  late DraftHandle h;
  late NotebookPage page;
  late DateTime clock;
  late String? owner;
  const device = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
  setUp(() async {
    f = AuthFixture();
    server = NotebookServer(f);
    await f.signIn();
    owner = f.cubit.draftOwner;
    clock = DateTime.utc(2026);
    queue = NotebookSyncQueue(
      f.cubit.drafts,
      NotebookApi(f.api),
      () => owner,
      clock: () => clock,
    );
    page = NotebookPage.fromJson(projection('one', 'a'), 'one', 'a');
    h = f.cubit.drafts.open(
      DraftKey(owner!, 'one', 'a'),
      revision: 0,
      rowVersion: page.rowVersion,
    );
    await h.ready;
    draw(h);
  });
  tearDown(() async {
    await queue.close();
    await f.cubit.close();
  });
  Future<void> enqueue() => queue.enqueue(h, page, device);

  test(
    'enqueue persists draft first and protects duplicate logical operation',
    () async {
      await enqueue();
      expect(h.saved, isTrue);
      expect(h.syncState, 'queued');
      final item = (await f.drafts.queued(owner!)).single;
      expect((await f.drafts.read(h.key))!.document.strokes.length, 1);
      expect(item.envelope.bytes, isNotEmpty);
      await f.drafts.enqueue(h.key, item.envelope, queue.now);
      await enqueue(); // Same unedited snapshot also coalesces.
      expect(await f.drafts.queued(owner!), hasLength(1));
      expect(server.mutations, 0);
    },
  );

  test('local save failure prevents enqueue and upload', () async {
    f.drafts.fail = true;
    await expectLater(enqueue(), throwsStateError);
    expect(f.drafts.queueRows, isEmpty);
    expect(server.mutations, 0);
    f.drafts.fail = false;
  });

  test('restart restores multiple snapshots and drains in page order with ACK chaining', () async {
    await enqueue();
    draw(h, pointer: 2);
    await enqueue();
    final before = await f.drafts.queued(owner!);
    expect(before, hasLength(2));
    expect(before.last.row['predecessorId'], before.first.id);
    expect(before.last.envelope.expectedRowVersion, page.rowVersion);
    await queue.close();
    await f.cubit.drafts.close();
    final reopened = LocalDrafts(f.drafts);
    queue = NotebookSyncQueue(
      reopened,
      NotebookApi(f.api),
      () => owner,
      clock: () => clock,
    );
    h = reopened.open(h.key);
    await h.ready;
    await queue.attach(h);
    expect(h.queuedCount, 2);
    await queue.drain();
    expect(server.mutations, 2);
    final requests = server.requests.where((r) => r.data is FormData).toList();
    expect(
      requests.map(
        (r) => Map.fromEntries((r.data as FormData).fields)['clientDraftId'],
      ),
      before.map((q) => q.id),
    );
    expect(
      Map.fromEntries(
        (requests.last.data as FormData).fields,
      )['expectedRowVersion'],
      projection('one', 'a', revision: 1)['rowVersion'],
    );
    expect(h.serverSynced, isTrue);
    expect(h.revision, 2);
    expect(await f.drafts.queued(owner!), isEmpty);
    await reopened.close();
  });

  test('network backoff survives reopen, bounds at five minutes and never busy loops', () async {
    await enqueue();
    server.fail = true;
    await queue.drain();
    var item = (await f.drafts.queued(owner!)).single;
    expect(item.state, 'offline');
    expect(item.attempts, 1);
    expect(item.due, queue.now + 5000);
    final requests = server.requests.length;
    await queue.drain();
    expect(server.requests.length, requests);
    for (var i = 0; i < 9; i++) {
      clock = DateTime.fromMillisecondsSinceEpoch(item.due, isUtc: true);
      await queue.drain();
      item = (await f.drafts.queued(owner!)).single;
      expect(item.due - queue.now, lessThanOrEqualTo(300000));
    }
    expect(item.due - queue.now, 300000);
    expect(h.saved, isTrue);
    expect(h.serverSynced, isFalse);
  });

  test('uncertain ACK retains exact item and retry removes only matching operation', () async {
    await enqueue();
    draw(h, pointer: 2);
    await enqueue();
    final items = await f.drafts.queued(owner!);
    server.loseReply = true;
    await queue.drain();
    expect(await f.drafts.queued(owner!), hasLength(2));
    expect(server.mutations, 1);
    expect(
      (await f.drafts.queued(owner!)).first.envelope.bytes,
      items.first.envelope.bytes,
    );
    await expectLater(
      f.drafts.acknowledge(items.last, page.rowVersion),
      throwsStateError,
    );
    expect(await f.drafts.queued(owner!), hasLength(2));
    await queue.drain(force: true);
    expect(server.mutations, 2);
    expect(await f.drafts.queued(owner!), isEmpty);
  });

  test('auth refresh replays exact multipart while draining', () async {
    await enqueue();
    f.backend.expiredAccess = true;
    await queue.drain();
    expect(f.backend.refreshes, 1);
    expect(f.backend.multipartBodies, hasLength(2));
    expect(f.backend.multipartBodies.first, f.backend.multipartBodies.last);
    expect(await f.drafts.queued(owner!), isEmpty);
  });

  for (final status in [403, 404, 409, 422]) {
    test('$status stops that page queue, including its successors', () async {
      await enqueue();
      draw(h, pointer: 2);
      await enqueue();
      var calls = 0;
      f.backend.notebook = (r) async {
        calls++;
        return f.backend.reply(status, {
          'code': status == 409 ? 'page_changed' : 'denied',
        });
      };
      await queue.drain();
      expect(calls, 1);
      await queue.drain(force: true);
      expect(calls, 1);
      final items = await f.drafts.queued(owner!);
      expect(items, hasLength(2));
      expect(items.first.state, status == 409 ? 'conflict' : 'failed');
      expect(items.last.attempts, 0);
      expect(h.syncState, status == 409 ? 'conflict' : 'syncFailed');
    });
  }

  test(
    'logout retains queue; other staff/server cannot see or process it',
    () async {
      await enqueue();
      final previous = owner!;
      expect(await f.cubit.logout(), isTrue);
      expect(f.cubit.state, isA<SessionUnauthenticated>());
      owner = null;
      queue.contextChanged();
      await queue.drain();
      owner = 'another-server-or-staff';
      queue.contextChanged();
      expect(await f.drafts.queued(owner!), isEmpty);
      await queue.drain();
      expect(server.mutations, 0);
      expect(await f.drafts.queued(previous), hasLength(1));
    },
  );

  test(
    'owner change during upload cannot remove item or publish late ACK',
    () async {
      await enqueue();
      final previous = owner!;
      server.gate = Completer<void>();
      final drain = queue.drain();
      await until(() => server.requests.isNotEmpty);
      owner = 'other';
      queue.contextChanged();
      server.gate!.complete();
      await drain;
      expect(await f.drafts.queued(previous), hasLength(1));
      expect(h.serverSynced, isFalse);
    },
  );

  test('foreground pause retains pending work; resume drains', () async {
    await enqueue();
    queue.setForeground(false);
    await queue.drain();
    expect(server.mutations, 0);
    queue.setForeground(true);
    await queue.drain();
    expect(server.mutations, 1);
  });

  test('foreground startup automatically drains persisted work', () async {
    await enqueue();
    queue.start();
    await until(() => h.serverSynced);
    expect(await f.drafts.queued(owner!), isEmpty);
  });

  test('patient/page binding mismatch fails closed without sending', () async {
    await enqueue();
    final row = f.drafts.queueRows.values.single;
    row['patientId'] = 'two';
    await queue.drain();
    expect(server.mutations, 0);
    expect(row['state'], 'failed');
    expect(f.drafts.queueRows, hasLength(1));
  });

  test(
    'missing local draft stops replay and retains clinical payload',
    () async {
      await enqueue();
      f.cubit.drafts.leave(h);
      await until(() => h.disposed);
      f.drafts.rows.remove(h.key);
      await queue.drain();
      expect(server.mutations, 0);
      expect((await f.drafts.queued(owner!)).single.state, 'failed');
    },
  );

  test('opening a page during drain keeps its live handle attached', () async {
    await enqueue();
    f.cubit.drafts.leave(h);
    await until(() => h.disposed);
    server.gate = Completer<void>();
    final drain = queue.drain();
    await until(() => server.requests.isNotEmpty);
    final opened = f.cubit.drafts.open(h.key);
    await opened.ready;
    server.gate!.complete();
    await drain;
    await Future<void>.delayed(Duration.zero);
    expect(opened.disposed, isFalse);
    expect(opened.attached, isTrue);
    expect(opened.serverSynced, isTrue);
  });

  test('queued patients use their own routes and acknowledgements', () async {
    await enqueue();
    final otherPage = NotebookPage.fromJson(projection('two', 'b'), 'two', 'b');
    server.pages['b'] = projection('two', 'b');
    final other = f.cubit.drafts.open(
      DraftKey(owner!, 'two', 'b'),
      revision: 0,
      rowVersion: otherPage.rowVersion,
    );
    await other.ready;
    draw(other);
    await queue.enqueue(other, otherPage, device);
    await queue.drain();
    final uploads = server.requests.where((r) => r.data is FormData).toList();
    expect(uploads.first.path, contains('/patients/one/notebook/pages/a/'));
    expect(uploads.last.path, contains('/patients/two/notebook/pages/b/'));
    expect(h.ink.document.patientId, 'one');
    expect(other.ink.document.patientId, 'two');
    expect(h.serverSynced, isTrue);
    expect(other.serverSynced, isTrue);
  });
}
