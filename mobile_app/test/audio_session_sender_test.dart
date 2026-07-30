import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:mobile_app/audio_session_sender.dart';

void main() {
  test('sends start control before buffered pre-roll audio', () {
    final events = <String>[];
    final sender = AudioSessionSender(
      sendStart: () {
        events.add('start');
        return true;
      },
      sendAudio: (bytes) {
        events.add('audio:${bytes.join(',')}');
      },
    );

    sender.addAudio(Uint8List.fromList([1, 2]));
    sender.addAudio(Uint8List.fromList([3, 4]));

    expect(events, isEmpty);
    expect(sender.start(), isTrue);
    sender.addAudio(Uint8List.fromList([5, 6]));

    expect(events, [
      'start',
      'audio:1,2',
      'audio:3,4',
      'audio:5,6',
    ]);
  });

  test('keeps pre-roll queued when start control cannot be sent', () {
    final events = <String>[];
    var canStart = false;
    final sender = AudioSessionSender(
      sendStart: () {
        events.add('start');
        return canStart;
      },
      sendAudio: (bytes) {
        events.add('audio:${bytes.join(',')}');
      },
    );

    sender.addAudio(Uint8List.fromList([7, 8]));
    expect(sender.start(), isFalse);

    canStart = true;
    expect(sender.start(), isTrue);

    expect(events, ['start', 'start', 'audio:7,8']);
  });
}
