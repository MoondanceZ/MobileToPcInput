import 'dart:typed_data';

class AudioTransport {
  static const sampleRate = 16000;
  static const bytesPerSample = 2;
  static const channelCount = 1;
  static const streamBufferSize = 1280;
  static const captureWindow = Duration(milliseconds: 40);

  static Uint8List buildFrame(int type, Uint8List payload) {
    final frame = Uint8List(5 + payload.length);
    ByteData.sublistView(frame, 0, 5)
      ..setUint8(0, type)
      ..setUint32(1, payload.length, Endian.big);
    frame.setRange(5, frame.length, payload);
    return frame;
  }
}
