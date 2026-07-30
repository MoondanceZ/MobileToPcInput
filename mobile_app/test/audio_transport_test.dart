import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:mobile_app/audio_transport.dart';

void main() {
  test('captures PCM in forty millisecond windows', () {
    expect(AudioTransport.captureWindow, const Duration(milliseconds: 40));
    expect(AudioTransport.streamBufferSize, 1280);
  });

  test('builds header and payload as one socket write', () {
    final frame = AudioTransport.buildFrame(2, Uint8List.fromList([7, 8, 9]));

    expect(frame, [2, 0, 0, 0, 3, 7, 8, 9]);
  });

  test('decodes a fragmented control frame', () {
    final decoder = AudioFrameDecoder();
    final frame = AudioTransport.buildFrame(
      1,
      Uint8List.fromList('accepted'.codeUnits),
    );

    expect(decoder.add(frame.sublist(0, 3)), isEmpty);
    final decoded = decoder.add(frame.sublist(3));

    expect(decoded, hasLength(1));
    expect(decoded.single.type, 1);
    expect(decoded.single.payload, 'accepted'.codeUnits);
  });

  test('decodes multiple frames from one socket chunk', () {
    final decoder = AudioFrameDecoder();
    final first = AudioTransport.buildFrame(1, Uint8List.fromList([1]));
    final second = AudioTransport.buildFrame(1, Uint8List.fromList([2, 3]));
    final chunk = Uint8List.fromList([...first, ...second]);

    final decoded = decoder.add(chunk);

    expect(decoded, hasLength(2));
    expect(decoded[0].payload, [1]);
    expect(decoded[1].payload, [2, 3]);
  });
}
