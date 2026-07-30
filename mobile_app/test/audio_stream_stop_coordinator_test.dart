import 'dart:async';

import 'package:flutter_test/flutter_test.dart';
import 'package:mobile_app/audio_stream_stop_coordinator.dart';

void main() {
  test('waits for final audio chunks before cancelling and flushing', () async {
    final events = <String>[];
    final streamDone = Completer<void>();

    final stopping = finishAudioStream(
      stopRecorder: () async {
        events.add('stop-recorder');
      },
      streamDone: streamDone.future,
      cancelSubscription: () async {
        events.add('cancel-subscription');
      },
      flushSocket: () async {
        events.add('flush-socket');
      },
      timeout: const Duration(seconds: 1),
    );

    await Future<void>.delayed(Duration.zero);
    expect(events, ['stop-recorder']);

    events.add('stream-done');
    streamDone.complete();
    await stopping;

    expect(events, [
      'stop-recorder',
      'stream-done',
      'cancel-subscription',
      'flush-socket',
    ]);
  });

  test('continues cleanup when stopping the recorder fails', () async {
    final events = <String>[];
    final errors = <Object>[];
    final streamDone = Completer<void>();

    await finishAudioStream(
      stopRecorder: () async {
        events.add('stop-recorder');
        throw StateError('Android recorder stop failed');
      },
      streamDone: streamDone.future,
      cancelSubscription: () async {
        events.add('cancel-subscription');
      },
      flushSocket: () async {
        events.add('flush-socket');
      },
      timeout: const Duration(seconds: 5),
      onError: errors.add,
    ).timeout(const Duration(milliseconds: 500));

    expect(events, ['stop-recorder', 'cancel-subscription', 'flush-socket']);
    expect(errors, hasLength(1));
    expect(errors.single, isA<StateError>());
  });
}
