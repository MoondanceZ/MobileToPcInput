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
}
