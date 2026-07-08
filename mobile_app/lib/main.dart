import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:math';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:record/record.dart';
import 'package:shared_preferences/shared_preferences.dart';

void main() {
  runApp(const MobileToPcInputApp());
}

class MobileToPcInputApp extends StatelessWidget {
  const MobileToPcInputApp({super.key});

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: '手机麦克风',
      theme: ThemeData(
        colorScheme: ColorScheme.fromSeed(
          seedColor: const Color(0xff2f6fed),
        ),
        useMaterial3: true,
      ),
      home: const MicrophoneBridgePage(),
    );
  }
}

class MicrophoneBridgePage extends StatefulWidget {
  const MicrophoneBridgePage({super.key});

  @override
  State<MicrophoneBridgePage> createState() => _MicrophoneBridgePageState();
}

class _MicrophoneBridgePageState extends State<MicrophoneBridgePage> {
  static const _hostKey = 'receiver_host';
  static const _portKey = 'receiver_port';

  final _hostController = TextEditingController();
  final _portController = TextEditingController(text: '8765');
  final _recorder = AudioRecorder();

  Socket? _socket;
  StreamSubscription<Uint8List>? _socketSubscription;
  StreamSubscription<Uint8List>? _audioSubscription;
  bool _isConnecting = false;
  bool _isConnected = false;
  bool _manualDisconnect = false;
  bool _isReconnecting = false;
  bool _isRecording = false;
  bool _isStoppingRecording = false;
  bool _isVocoTypeHeld = false;
  double _level = 0;
  String _status = '未连接';

  @override
  void initState() {
    super.initState();
    _loadSettings();
  }

  Future<void> _loadSettings() async {
    final prefs = await SharedPreferences.getInstance();
    _hostController.text = prefs.getString(_hostKey) ?? '';
    _portController.text = prefs.getString(_portKey) ?? '8765';
  }

  Future<void> _saveSettings() async {
    final prefs = await SharedPreferences.getInstance();
    await prefs.setString(_hostKey, _hostController.text.trim());
    await prefs.setString(_portKey, _portController.text.trim());
  }

  Future<void> _connect() async {
    final host = _hostController.text.trim();
    final port = int.tryParse(_portController.text.trim());
    if (host.isEmpty || port == null || port <= 0 || port > 65535) {
      _setStatus('请输入正确的电脑 IP 和端口');
      return;
    }

    setState(() {
      _isConnecting = true;
      _manualDisconnect = false;
      _status = '正在连接...';
    });

    try {
      final socket = await Socket.connect(host, port)
          .timeout(const Duration(seconds: 5));
      await _socketSubscription?.cancel();
      _socketSubscription = socket.listen(
        (_) {},
        onError: (Object error) {
          _handleSocketClosed('连接中断，正在重连...');
        },
        onDone: () {
          _handleSocketClosed(_manualDisconnect ? '未连接' : '正在重连...');
        },
        cancelOnError: true,
      );

      await _saveSettings();
      setState(() {
        _socket = socket;
        _isConnected = true;
        _status = '已连接';
      });
    } catch (ex) {
      _setStatus('连接失败: $ex');
    } finally {
      if (mounted) {
        setState(() => _isConnecting = false);
      }
    }
  }

  Future<void> _disconnect() async {
    _manualDisconnect = true;
    await _stopRecording();
    await _socketSubscription?.cancel();
    _socketSubscription = null;
    await _socket?.close();
    if (!mounted) {
      return;
    }

    setState(() {
      _socket = null;
      _isConnected = false;
      _status = '未连接';
      _level = 0;
    });
  }

  Future<void> _startRecording() async {
    if (!_isConnected || _socket == null || _isRecording) {
      return;
    }

    unawaited(HapticFeedback.mediumImpact());
    if (!await _recorder.hasPermission()) {
      _setStatus('没有麦克风权限');
      return;
    }

    try {
      if (_sendControlMessage('vocotype-start')) {
        _isVocoTypeHeld = true;
      }
      await Future<void>.delayed(const Duration(milliseconds: 120));
      final stream = await _recorder.startStream(
        const RecordConfig(
          encoder: AudioEncoder.pcm16bits,
          sampleRate: 16000,
          numChannels: 1,
          autoGain: true,
          echoCancel: false,
          noiseSuppress: true,
        ),
      );

      _audioSubscription = stream.listen(
        (chunk) {
          _sendFrame(2, chunk);
          final level = _calculateLevel(chunk);
          if (mounted) {
            setState(() => _level = level);
          }
        },
        onError: (Object error) {
          _setStatus('录音错误: $error');
          unawaited(_stopRecording());
        },
        cancelOnError: true,
      );

      setState(() {
        _isRecording = true;
        _status = '正在发送语音';
      });
    } catch (ex) {
      _setStatus('启动录音失败: $ex');
    }
  }

  Future<void> _reconnect() async {
    if (_isReconnecting || _manualDisconnect || !mounted) {
      return;
    }

    _isReconnecting = true;
    for (var attempt = 1; mounted && !_manualDisconnect; attempt++) {
      final waitMs = min(5000, 500 * attempt);
      await Future<void>.delayed(Duration(milliseconds: waitMs));
      if (_isConnected || _isConnecting) {
        break;
      }

      await _connect();
      if (_isConnected) {
        break;
      }
    }

    _isReconnecting = false;
  }

  void _handleSocketClosed(String status) {
    if (!mounted || _socket == null) {
      return;
    }

    unawaited(_stopRecording());
    setState(() {
      _socket = null;
      _isConnected = false;
      _status = status;
    });
    if (!_manualDisconnect) {
      unawaited(_reconnect());
    }
  }

  bool _sendControlMessage(String type) {
    final socket = _socket;
    if (socket == null) {
      _setStatus('未连接电脑，无法启动 VocoType');
      return false;
    }

    final payload = Uint8List.fromList(utf8.encode(jsonEncode({'type': type})));
    _sendFrame(1, payload);
    return true;
  }

  void _sendFrame(int type, Uint8List payload) {
    final socket = _socket;
    if (socket == null) {
      return;
    }

    final header = ByteData(5)
      ..setUint8(0, type)
      ..setUint32(1, payload.length, Endian.big);
    socket.add(header.buffer.asUint8List());
    socket.add(payload);
  }

  Future<void> _stopRecording() async {
    if (_isStoppingRecording) {
      return;
    }

    _isStoppingRecording = true;
    unawaited(HapticFeedback.lightImpact());

    if (!_isRecording && _audioSubscription == null && !_isVocoTypeHeld) {
      _isStoppingRecording = false;
      return;
    }

    try {
      if (_isRecording || _audioSubscription != null) {
        await _recorder.stop();
        await Future<void>.delayed(const Duration(milliseconds: 180));
        await _audioSubscription?.cancel();
        _audioSubscription = null;
        await _socket?.flush();
      }

      if (_isVocoTypeHeld) {
        _sendControlMessage('vocotype-stop');
        await _socket?.flush();
        _isVocoTypeHeld = false;
      }

      if (mounted) {
        setState(() {
          _isRecording = false;
          _level = 0;
          _status = _isConnected ? '已连接' : '未连接';
        });
      }
    } finally {
      _isStoppingRecording = false;
    }
  }

  double _calculateLevel(Uint8List bytes) {
    if (bytes.length < 2) {
      return 0;
    }

    var sum = 0.0;
    var samples = 0;
    final data = ByteData.sublistView(bytes);
    for (var i = 0; i + 1 < bytes.length; i += 2) {
      final sample = data.getInt16(i, Endian.little) / 32768.0;
      sum += sample * sample;
      samples++;
    }

    return samples == 0 ? 0 : min(1, sqrt(sum / samples) * 4);
  }

  void _setStatus(String message) {
    if (mounted) {
      setState(() => _status = message);
    }
  }

  @override
  void dispose() {
    _audioSubscription?.cancel();
    _socketSubscription?.cancel();
    _recorder.dispose();
    _socket?.close();
    _hostController.dispose();
    _portController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final canTalk = _isConnected && !_isConnecting;

    return Scaffold(
      appBar: AppBar(
        title: const Text('手机麦克风'),
      ),
      body: SafeArea(
        child: ListView(
          padding: const EdgeInsets.all(20),
          children: [
            Text(
              '电脑接收器',
              style: Theme.of(context).textTheme.titleLarge,
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _hostController,
              keyboardType: TextInputType.number,
              decoration: const InputDecoration(
                labelText: '电脑 IP',
                hintText: '例如 192.168.1.20',
                border: OutlineInputBorder(),
              ),
              enabled: !_isConnected && !_isConnecting,
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _portController,
              keyboardType: TextInputType.number,
              decoration: const InputDecoration(
                labelText: '端口',
                border: OutlineInputBorder(),
              ),
              enabled: !_isConnected && !_isConnecting,
            ),
            const SizedBox(height: 16),
            FilledButton.icon(
              onPressed: _isConnecting
                  ? null
                  : _isConnected
                      ? _disconnect
                      : _connect,
              icon: Icon(_isConnected ? Icons.link_off : Icons.wifi),
              label: Text(_isConnected ? '断开连接' : '连接电脑'),
            ),
            const SizedBox(height: 24),
            _StatusPanel(
              status: _status,
              isConnected: _isConnected,
              isRecording: _isRecording,
              level: _level,
            ),
            const SizedBox(height: 32),
            GestureDetector(
              onTapDown: canTalk ? (_) => _startRecording() : null,
              onTapUp: canTalk ? (_) => _stopRecording() : null,
              onTapCancel: canTalk ? _stopRecording : null,
              child: AnimatedContainer(
                duration: const Duration(milliseconds: 120),
                height: 176,
                decoration: BoxDecoration(
                  shape: BoxShape.circle,
                  color: _isRecording
                      ? const Color(0xffe54646)
                      : canTalk
                          ? const Color(0xff2f6fed)
                          : Colors.grey.shade300,
                  boxShadow: _isRecording
                      ? [
                          BoxShadow(
                            color: const Color(0xffe54646)
                                .withValues(alpha: 0.3),
                            blurRadius: 24,
                            spreadRadius: 8,
                          ),
                        ]
                      : null,
                ),
                alignment: Alignment.center,
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Icon(
                      Icons.mic,
                      color: canTalk ? Colors.white : Colors.grey.shade600,
                      size: 56,
                    ),
                    const SizedBox(height: 8),
                    Text(
                      _isRecording ? '松开停止' : '按住说话',
                      style: TextStyle(
                        color: canTalk ? Colors.white : Colors.grey.shade700,
                        fontSize: 18,
                        fontWeight: FontWeight.w700,
                      ),
                    ),
                  ],
                ),
              ),
            ),
            const SizedBox(height: 24),
            Text(
              '请先在电脑端启动接收器，并让手机和电脑连接同一个 WiFi。',
              textAlign: TextAlign.center,
              style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                    color: Colors.grey.shade700,
                  ),
            ),
          ],
        ),
      ),
    );
  }
}

class _StatusPanel extends StatelessWidget {
  const _StatusPanel({
    required this.status,
    required this.isConnected,
    required this.isRecording,
    required this.level,
  });

  final String status;
  final bool isConnected;
  final bool isRecording;
  final double level;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        color: Colors.grey.shade100,
        borderRadius: BorderRadius.circular(8),
        border: Border.all(color: Colors.grey.shade300),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Icon(
                isConnected ? Icons.check_circle : Icons.info,
                color: isConnected ? Colors.green : Colors.grey,
              ),
              const SizedBox(width: 8),
              Expanded(child: Text(status)),
            ],
          ),
          const SizedBox(height: 12),
          LinearProgressIndicator(
            value: isRecording ? level : 0,
            minHeight: 10,
            borderRadius: BorderRadius.circular(999),
          ),
        ],
      ),
    );
  }
}
