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
}
