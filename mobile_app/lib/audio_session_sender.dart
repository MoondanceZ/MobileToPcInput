import 'dart:typed_data';

class AudioSessionSender {
  AudioSessionSender({
    required bool Function() sendStart,
    required void Function(Uint8List bytes) sendAudio,
  }) : _sendStart = sendStart,
       _sendAudio = sendAudio;

  final bool Function() _sendStart;
  final void Function(Uint8List bytes) _sendAudio;
  final List<Uint8List> _preRoll = [];
  bool _isStarted = false;

  bool get isStarted => _isStarted;

  void addAudio(Uint8List bytes) {
    if (bytes.isEmpty) {
      return;
    }

    if (_isStarted) {
      _sendAudio(bytes);
      return;
    }

    _preRoll.add(Uint8List.fromList(bytes));
  }

  bool start() {
    if (_isStarted) {
      return true;
    }

    if (!_sendStart()) {
      return false;
    }

    _isStarted = true;
    for (final bytes in _preRoll) {
      _sendAudio(bytes);
    }
    _preRoll.clear();
    return true;
  }

  void discard() {
    _preRoll.clear();
  }
}
