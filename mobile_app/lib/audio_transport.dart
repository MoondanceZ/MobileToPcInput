import 'dart:typed_data';

class AudioTransport {
  static const headerLength = 5;
  static const maxPayloadLength = 1024 * 1024;
  static const sampleRate = 16000;
  static const bytesPerSample = 2;
  static const channelCount = 1;
  static const streamBufferSize = 1280;
  static const captureWindow = Duration(milliseconds: 40);

  static Uint8List buildFrame(int type, Uint8List payload) {
    final frame = Uint8List(headerLength + payload.length);
    ByteData.sublistView(frame, 0, headerLength)
      ..setUint8(0, type)
      ..setUint32(1, payload.length, Endian.big);
    frame.setRange(headerLength, frame.length, payload);
    return frame;
  }
}

class AudioTransportFrame {
  const AudioTransportFrame({required this.type, required this.payload});

  final int type;
  final Uint8List payload;
}

class AudioFrameDecoder {
  Uint8List _buffer = Uint8List(0);

  List<AudioTransportFrame> add(Uint8List chunk) {
    if (chunk.isEmpty) {
      return const [];
    }

    _buffer = Uint8List.fromList([..._buffer, ...chunk]);
    final frames = <AudioTransportFrame>[];
    var offset = 0;
    while (_buffer.length - offset >= AudioTransport.headerLength) {
      final header = ByteData.sublistView(
        _buffer,
        offset,
        offset + AudioTransport.headerLength,
      );
      final type = header.getUint8(0);
      final payloadLength = header.getUint32(1, Endian.big);
      if (payloadLength > AudioTransport.maxPayloadLength) {
        _buffer = Uint8List(0);
        throw const FormatException('TCP frame payload is too large');
      }

      final frameLength = AudioTransport.headerLength + payloadLength;
      if (_buffer.length - offset < frameLength) {
        break;
      }

      final payloadStart = offset + AudioTransport.headerLength;
      frames.add(
        AudioTransportFrame(
          type: type,
          payload: Uint8List.fromList(
            _buffer.sublist(payloadStart, payloadStart + payloadLength),
          ),
        ),
      );
      offset += frameLength;
    }

    if (offset > 0) {
      _buffer = Uint8List.fromList(_buffer.sublist(offset));
    }
    return frames;
  }
}
