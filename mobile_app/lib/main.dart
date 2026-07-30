import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:math';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:mobile_scanner/mobile_scanner.dart';
import 'package:record/record.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'audio_session_sender.dart';
import 'audio_stream_stop_coordinator.dart';
import 'audio_transport.dart';

const _appBackgroundColor = Color(0xffeef6ff);

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  await SystemChrome.setPreferredOrientations([DeviceOrientation.portraitUp]);
  SystemChrome.setSystemUIOverlayStyle(
    const SystemUiOverlayStyle(
      statusBarColor: _appBackgroundColor,
      statusBarIconBrightness: Brightness.dark,
      statusBarBrightness: Brightness.light,
      systemNavigationBarColor: _appBackgroundColor,
      systemNavigationBarIconBrightness: Brightness.dark,
    ),
  );
  runApp(const MobileToPcInputApp());
}

class MobileToPcInputApp extends StatelessWidget {
  const MobileToPcInputApp({super.key});

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: '手机语音输入',
      theme: ThemeData(
        colorScheme: ColorScheme.fromSeed(seedColor: const Color(0xff2f6fed)),
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

class _MicrophoneBridgePageState extends State<MicrophoneBridgePage>
    with WidgetsBindingObserver {
  static const _hostKey = 'receiver_host';
  static const _portKey = 'receiver_port';
  static const _deepLinkChannel = MethodChannel('mobile_to_pc_input/deep_link');
  static const _keepScreenOnIdleTimeout = Duration(minutes: 10);
  static const _sessionHandshakeTimeout = Duration(seconds: 2);

  final _hostController = TextEditingController();
  final _portController = TextEditingController(text: '8765');
  final _recorder = AudioRecorder();

  Socket? _socket;
  StreamSubscription<Uint8List>? _socketSubscription;
  StreamSubscription<Uint8List>? _audioSubscription;
  Completer<void>? _audioStreamDone;
  Completer<void>? _recordingStartDone;
  AudioSessionSender? _audioSessionSender;
  bool _isConnecting = false;
  bool _isConnected = false;
  bool _manualDisconnect = false;
  bool _isReconnecting = false;
  bool _isStartingRecording = false;
  bool _isRecording = false;
  bool _isStoppingRecording = false;
  bool _isHandlingSocketClosed = false;
  bool _isAsrSessionActive = false;
  bool _isKeepScreenOn = false;
  bool _isKeepScreenIdleTimedOut = false;
  bool _isTalkPressed = false;
  int? _activeTalkPointer;
  int _talkSessionGeneration = 0;
  Timer? _keepScreenIdleTimer;
  double _level = 0;
  String _status = '未连接';

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    _deepLinkChannel.setMethodCallHandler(_handleDeepLinkCall);
    _hostController.addListener(_handleUserInput);
    _portController.addListener(_handleUserInput);
    _loadSettings();
  }

  Future<void> _loadSettings() async {
    final prefs = await SharedPreferences.getInstance();
    _hostController.text = prefs.getString(_hostKey) ?? '';
    _portController.text = prefs.getString(_portKey) ?? '8765';
    var didApplyInitialLink = false;
    try {
      final initialLink = await _deepLinkChannel.invokeMethod<String?>(
        'getInitialLink',
      );
      if (initialLink != null && initialLink.isNotEmpty) {
        didApplyInitialLink = await _applyConnectLink(initialLink);
      }
    } on MissingPluginException {
      // The deep-link channel only exists on Android.
    }

    if (!didApplyInitialLink && _hasValidReceiverEndpoint()) {
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted && !_isConnected && !_isConnecting) {
          unawaited(_connect());
        }
      });
    }
  }

  Future<dynamic> _handleDeepLinkCall(MethodCall call) async {
    if (call.method == 'onLink' && call.arguments is String) {
      await _applyConnectLink(call.arguments as String);
    }
  }

  Future<bool> _applyConnectLink(String link) async {
    final uri = Uri.tryParse(link);
    final host = uri?.queryParameters['host'];
    final port = uri?.queryParameters['port'];
    if (uri?.scheme != 'mobiletopcinput' ||
        uri?.host != 'connect' ||
        host == null ||
        port == null) {
      return false;
    }

    if (_isConnected) {
      await _disconnect();
    }

    _hostController.text = host;
    _portController.text = port;
    await _saveSettings();
    await _connect();
    return true;
  }

  Future<void> _saveSettings() async {
    final prefs = await SharedPreferences.getInstance();
    await prefs.setString(_hostKey, _hostController.text.trim());
    await prefs.setString(_portKey, _portController.text.trim());
  }

  bool _hasValidReceiverEndpoint() {
    final host = _hostController.text.trim();
    final port = int.tryParse(_portController.text.trim());
    return host.isNotEmpty && port != null && port > 0 && port <= 65535;
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
    _syncKeepScreenOn();

    Socket? connectingSocket;
    try {
      final socket = await Socket.connect(
        host,
        port,
      ).timeout(const Duration(seconds: 5));
      connectingSocket = socket;
      socket.setOption(SocketOption.tcpNoDelay, true);
      await _socketSubscription?.cancel();
      final sessionStatus = Completer<String>();
      final decoder = AudioFrameDecoder();
      var sessionAccepted = false;
      _socket = socket;
      _socketSubscription = socket.listen(
        (chunk) {
          try {
            for (final frame in decoder.add(chunk)) {
              if (frame.type != 1) {
                continue;
              }

              final message =
                  jsonDecode(utf8.decode(frame.payload))
                      as Map<String, dynamic>;
              final type = message['type'] as String?;
              if (type == 'session-accepted') {
                sessionAccepted = true;
                if (!sessionStatus.isCompleted) {
                  sessionStatus.complete(type);
                }
              } else if (type == 'session-rejected' &&
                  !sessionStatus.isCompleted) {
                sessionStatus.complete(type);
              }
            }
          } catch (error) {
            if (!sessionStatus.isCompleted) {
              sessionStatus.completeError(error);
            }
          }
        },
        onError: (Object error) {
          if (!sessionStatus.isCompleted) {
            sessionStatus.completeError(error);
          } else if (sessionAccepted) {
            _handleSocketClosed(socket, '连接中断，正在重连...');
          }
        },
        onDone: () {
          if (!sessionStatus.isCompleted) {
            sessionStatus.complete('session-closed');
          } else if (sessionAccepted) {
            _handleSocketClosed(socket, _manualDisconnect ? '未连接' : '正在重连...');
          }
        },
        cancelOnError: true,
      );

      final status = await sessionStatus.future.timeout(
        _sessionHandshakeTimeout,
      );
      if (status == 'session-rejected') {
        await _closeSocket(socket);
        _setStatus('电脑已有其他手机连接');
        if (!_manualDisconnect && !_isReconnecting) {
          unawaited(_reconnect());
        }
        return;
      }

      if (status != 'session-accepted' ||
          !sessionAccepted ||
          !identical(_socket, socket)) {
        throw const SocketException('电脑未确认语音会话连接');
      }

      await _saveSettings();
      setState(() {
        _isConnected = true;
        _status = '已连接';
      });
      _syncKeepScreenOn();
    } catch (ex) {
      debugPrint('Connect failed: $ex');
      if (connectingSocket != null) {
        await _closeSocket(connectingSocket);
      }
      _setStatus('连接失败');
    } finally {
      if (mounted) {
        setState(() => _isConnecting = false);
        _syncKeepScreenOn();
      }
    }
  }

  Future<void> _closeSocket(Socket socket) async {
    if (identical(_socket, socket)) {
      _socket = null;
      final subscription = _socketSubscription;
      _socketSubscription = null;
      await subscription?.cancel();
    }

    try {
      await socket.close();
    } catch (_) {
      socket.destroy();
    }
    if (mounted) {
      setState(() => _isConnected = false);
    } else {
      _isConnected = false;
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
    _syncKeepScreenOn();
  }

  bool _isCurrentTalkSession(int generation) {
    return generation == _talkSessionGeneration &&
        _isTalkPressed &&
        _activeTalkPointer != null &&
        _isConnected &&
        _socket != null;
  }

  Future<void> _startRecording(int generation) async {
    if (!_isConnected ||
        _socket == null ||
        _isRecording ||
        _isStartingRecording ||
        !_isCurrentTalkSession(generation)) {
      return;
    }

    _isStartingRecording = true;
    final recordingStartDone = Completer<void>();
    _recordingStartDone = recordingStartDone;

    try {
      unawaited(HapticFeedback.mediumImpact());
      if (!await _recorder.hasPermission()) {
        _setStatus('没有麦克风权限');
        return;
      }

      if (!_isCurrentTalkSession(generation)) {
        return;
      }

      final sessionSender = AudioSessionSender(
        sendStart: () => _sendControlMessage('asr-start'),
        sendAudio: (bytes) => _sendFrame(2, bytes),
      );
      _audioSessionSender = sessionSender;
      if (!sessionSender.start()) {
        throw const SocketException('无法启动电脑端语音会话');
      }
      _isAsrSessionActive = true;
      await _socket?.flush().timeout(const Duration(milliseconds: 800));

      if (!_isCurrentTalkSession(generation)) {
        return;
      }

      final stream = await _recorder.startStream(
        const RecordConfig(
          encoder: AudioEncoder.pcm16bits,
          sampleRate: AudioTransport.sampleRate,
          numChannels: AudioTransport.channelCount,
          autoGain: true,
          echoCancel: false,
          noiseSuppress: true,
          streamBufferSize: AudioTransport.streamBufferSize,
        ),
      );

      if (!_isCurrentTalkSession(generation)) {
        try {
          await _recorder.stop().timeout(const Duration(milliseconds: 800));
        } catch (_) {
          // The matching stop flow still releases the PC bridge session.
        }
        return;
      }

      _audioStreamDone = Completer<void>();
      _audioSubscription = stream.listen(
        (chunk) {
          sessionSender.addAudio(chunk);
          final level = _calculateLevel(chunk);
          if (mounted) {
            setState(() => _level = level);
          }
        },
        onError: (Object error) {
          _completeAudioStream();
          _setStatus('录音错误: $error');
          unawaited(_stopRecording());
        },
        onDone: () {
          _completeAudioStream();
        },
        cancelOnError: true,
      );

      if (_isCurrentTalkSession(generation) && mounted) {
        setState(() {
          _isRecording = true;
          _status = '正在发送语音';
        });
      }
    } catch (ex) {
      if (_isCurrentTalkSession(generation)) {
        _setStatus('启动录音失败: $ex');
      }
      if (_isAsrSessionActive) {
        _sendControlMessage('asr-stop');
        try {
          await _socket?.flush().timeout(const Duration(milliseconds: 800));
        } catch (_) {
          // Socket shutdown will also release the desktop bridge session.
        }
        _isAsrSessionActive = false;
      }
      _audioSessionSender?.discard();
      _audioSessionSender = null;
    } finally {
      _isStartingRecording = false;
      if (!recordingStartDone.isCompleted) {
        recordingStartDone.complete();
      }
      if (identical(_recordingStartDone, recordingStartDone)) {
        _recordingStartDone = null;
      }
    }
  }

  Future<void> _reconnect() async {
    if (_isReconnecting || _manualDisconnect || !mounted) {
      return;
    }

    setState(() => _isReconnecting = true);
    _syncKeepScreenOn();
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

    if (mounted) {
      setState(() => _isReconnecting = false);
      _syncKeepScreenOn();
    } else {
      _isReconnecting = false;
    }
  }

  void _handleSocketClosed(Socket closedSocket, String status) {
    if (!mounted ||
        !identical(_socket, closedSocket) ||
        _isHandlingSocketClosed) {
      return;
    }

    _isHandlingSocketClosed = true;
    unawaited(_stopRecording());
    setState(() {
      _socket = null;
      _isConnected = false;
      _status = status;
    });
    _syncKeepScreenOn();
    if (!_manualDisconnect) {
      unawaited(_reconnect());
    }
    _isHandlingSocketClosed = false;
  }

  bool _sendControlMessage(String type) {
    final socket = _socket;
    if (socket == null) {
      _setStatus('未连接电脑，无法启动识别');
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

    try {
      socket.add(AudioTransport.buildFrame(type, payload));
    } catch (_) {
      _handleSocketClosed(socket, '正在重连...');
    }
  }

  Future<void> _stopRecording() async {
    _activeTalkPointer = null;
    _isTalkPressed = false;
    _talkSessionGeneration++;

    if (_isStoppingRecording) {
      return;
    }

    _isStoppingRecording = true;
    final wasRecording = _isRecording;
    _isRecording = false;
    _level = 0;
    if (mounted) {
      setState(() {
        _status = _isConnected ? '正在结束语音...' : '未连接';
      });
    }

    final recordingStartDone = _recordingStartDone;
    if (_isStartingRecording && recordingStartDone != null) {
      try {
        await recordingStartDone.future.timeout(const Duration(seconds: 2));
      } on TimeoutException {
        try {
          await _recorder.stop().timeout(const Duration(milliseconds: 800));
        } catch (_) {
          // Continue with bridge cleanup even if Android's recorder is stuck.
        }
      }
    }

    try {
      if (wasRecording || _audioSubscription != null) {
        final subscription = _audioSubscription;
        final streamDone = _audioStreamDone?.future ?? Future<void>.value();
        await finishAudioStream(
          stopRecorder: () async {
            await _recorder.stop();
          },
          streamDone: streamDone,
          cancelSubscription: () async {
            await subscription?.cancel();
          },
          flushSocket: () async {
            await _socket?.flush();
          },
          onError: (error) {
            debugPrint('Recording cleanup warning: $error');
          },
        );
        _audioSubscription = null;
        _audioStreamDone = null;
      }

      if (_isAsrSessionActive) {
        _sendControlMessage('asr-stop');
        try {
          await _socket?.flush().timeout(const Duration(milliseconds: 800));
        } catch (_) {
          // A disconnected socket cannot retain the desktop hotkey.
        }
        _isAsrSessionActive = false;
      }
    } finally {
      _audioSessionSender?.discard();
      _audioSessionSender = null;
      _audioSubscription = null;
      _audioStreamDone = null;
      _isAsrSessionActive = false;
      _isRecording = false;
      _level = 0;
      _isStoppingRecording = false;
      if (mounted) {
        setState(() {
          _status = _isConnected
              ? '已连接'
              : _isReconnecting
              ? '正在重连...'
              : '未连接';
        });
      }
    }
  }

  void _completeAudioStream() {
    final completer = _audioStreamDone;
    if (completer != null && !completer.isCompleted) {
      completer.complete();
    }
  }

  void _handleTalkPointerDown(PointerDownEvent event) {
    _handleUserInput();
    if (!_isConnected ||
        _isConnecting ||
        _isReconnecting ||
        _activeTalkPointer != null) {
      return;
    }

    _activeTalkPointer = event.pointer;
    final generation = ++_talkSessionGeneration;
    setState(() => _isTalkPressed = true);
    unawaited(_startRecording(generation));
  }

  void _handleTalkPointerEnd(PointerEvent event) {
    _handleUserInput();
    if (_activeTalkPointer != event.pointer) {
      return;
    }

    _activeTalkPointer = null;
    _talkSessionGeneration++;
    setState(() => _isTalkPressed = false);
    unawaited(HapticFeedback.lightImpact());
    unawaited(_stopRecording());
  }

  void _forceEndTalkSession() {
    if (!_isTalkPressed &&
        !_isStartingRecording &&
        !_isRecording &&
        !_isAsrSessionActive) {
      return;
    }

    _activeTalkPointer = null;
    _talkSessionGeneration++;
    if (mounted && _isTalkPressed) {
      setState(() => _isTalkPressed = false);
    } else {
      _isTalkPressed = false;
    }
    unawaited(_stopRecording());
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (state != AppLifecycleState.resumed) {
      _forceEndTalkSession();
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

  bool get _shouldKeepScreenOnForConnection =>
      _isConnecting || _isConnected || _isReconnecting;

  void _handleUserInput() {
    if (!mounted) {
      return;
    }

    final wasIdleTimedOut = _isKeepScreenIdleTimedOut;
    _isKeepScreenIdleTimedOut = false;
    _scheduleKeepScreenIdleTimer();
    if (wasIdleTimedOut) {
      _syncKeepScreenOn();
    }
  }

  void _scheduleKeepScreenIdleTimer() {
    _keepScreenIdleTimer?.cancel();
    if (!_shouldKeepScreenOnForConnection || _isKeepScreenIdleTimedOut) {
      return;
    }

    _keepScreenIdleTimer = Timer(_keepScreenOnIdleTimeout, () {
      if (!mounted || !_shouldKeepScreenOnForConnection) {
        return;
      }

      _isKeepScreenIdleTimedOut = true;
      _syncKeepScreenOn();
    });
  }

  void _syncKeepScreenOn() {
    if (!_shouldKeepScreenOnForConnection) {
      _keepScreenIdleTimer?.cancel();
      _keepScreenIdleTimer = null;
      _isKeepScreenIdleTimedOut = false;
    } else {
      _scheduleKeepScreenIdleTimer();
    }

    final shouldKeepScreenOn =
        _shouldKeepScreenOnForConnection && !_isKeepScreenIdleTimedOut;
    if (_isKeepScreenOn == shouldKeepScreenOn) {
      return;
    }

    _isKeepScreenOn = shouldKeepScreenOn;
    unawaited(
      _deepLinkChannel
          .invokeMethod<void>('setKeepScreenOn', shouldKeepScreenOn)
          .catchError((Object _) {}),
    );
  }

  Future<void> _openQrScanner() async {
    final code = await Navigator.of(
      context,
    ).push<String>(MaterialPageRoute(builder: (_) => const _QrScannerPage()));
    if (!mounted || code == null || code.isEmpty) {
      return;
    }

    final applied = await _applyConnectLink(code);
    if (!applied && mounted) {
      ScaffoldMessenger.of(
        context,
      ).showSnackBar(const SnackBar(content: Text('二维码不是有效的电脑连接地址。')));
    }
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    _keepScreenIdleTimer?.cancel();
    if (_isKeepScreenOn) {
      unawaited(
        _deepLinkChannel
            .invokeMethod<void>('setKeepScreenOn', false)
            .catchError((Object _) {}),
      );
    }
    _audioSubscription?.cancel();
    _socketSubscription?.cancel();
    _recorder.dispose();
    _socket?.close();
    _hostController.removeListener(_handleUserInput);
    _portController.removeListener(_handleUserInput);
    _hostController.dispose();
    _portController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final isConnectionBusy = _isConnecting || _isReconnecting;
    final canTalk = _isConnected && !isConnectionBusy;

    return Listener(
      behavior: HitTestBehavior.translucent,
      onPointerDown: (_) => _handleUserInput(),
      onPointerMove: (_) => _handleUserInput(),
      child: Scaffold(
        backgroundColor: _appBackgroundColor,
        body: SafeArea(
          child: LayoutBuilder(
            builder: (context, constraints) {
              final isCompact = constraints.maxHeight < 720;
              final isVeryCompact = constraints.maxHeight < 620;
              final verticalPadding = isVeryCompact
                  ? 10.0
                  : isCompact
                  ? 22.0
                  : 46.0;
              final buttonSize = isVeryCompact
                  ? 132.0
                  : isCompact
                  ? 168.0
                  : 188.0;

              return Padding(
                padding: EdgeInsets.fromLTRB(20, verticalPadding, 20, 28),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    _buildReceiverCard(
                      context,
                      isConnectionBusy,
                      isCompact: isVeryCompact,
                    ),
                    SizedBox(
                      height: isVeryCompact
                          ? 10
                          : isCompact
                          ? 16
                          : 22,
                    ),
                    _StatusPanel(
                      status: _status,
                      isConnected: _isConnected,
                      isConnecting: _isConnecting,
                      isReconnecting: _isReconnecting,
                      isRecording: _isRecording,
                      level: _level,
                      onRetry: _connect,
                    ),
                    const Spacer(),
                    _buildTalkButton(canTalk: canTalk, size: buttonSize),
                    const Spacer(),
                    if (!isVeryCompact)
                      Text(
                        _isConnected ? '手机语音将发送到电脑当前输入方式' : '连接电脑后启用语音输入',
                        textAlign: TextAlign.center,
                        style: const TextStyle(
                          color: Color(0xff718096),
                          fontSize: 16,
                          fontWeight: FontWeight.w500,
                        ),
                      ),
                  ],
                ),
              );
            },
          ),
        ),
      ),
    );
  }

  Widget _buildReceiverCard(
    BuildContext context,
    bool isConnectionBusy, {
    required bool isCompact,
  }) {
    return _SoftCard(
      padding: isCompact
          ? const EdgeInsets.fromLTRB(16, 14, 16, 14)
          : const EdgeInsets.fromLTRB(20, 20, 20, 18),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  '电脑接收器',
                  style: Theme.of(context).textTheme.headlineSmall?.copyWith(
                    color: const Color(0xff10213b),
                    fontWeight: FontWeight.w900,
                  ),
                ),
              ),
              FilledButton.icon(
                onPressed: isConnectionBusy ? null : _openQrScanner,
                icon: const Icon(Icons.qr_code_scanner, size: 22),
                label: const Text('扫码'),
                style: FilledButton.styleFrom(
                  backgroundColor: const Color(0xffe3e8ff),
                  disabledBackgroundColor: const Color(0xffe4e4e4),
                  foregroundColor: const Color(0xff10213b),
                  disabledForegroundColor: const Color(0xff9a9a9a),
                  elevation: 0,
                  padding: const EdgeInsets.symmetric(
                    horizontal: 18,
                    vertical: 13,
                  ),
                  textStyle: const TextStyle(
                    fontSize: 17,
                    fontWeight: FontWeight.w700,
                  ),
                ),
              ),
            ],
          ),
          SizedBox(height: isCompact ? 14 : 22),
          Row(
            crossAxisAlignment: CrossAxisAlignment.end,
            children: [
              Expanded(
                flex: 9,
                child: _SoftTextField(
                  controller: _hostController,
                  enabled: !_isConnected && !isConnectionBusy,
                  labelText: '电脑 IP',
                  hintText: '例如 192.168.1.20',
                ),
              ),
              const SizedBox(width: 12),
              Expanded(
                flex: 5,
                child: _SoftTextField(
                  controller: _portController,
                  enabled: !_isConnected && !isConnectionBusy,
                  labelText: '端口',
                ),
              ),
            ],
          ),
          SizedBox(height: isCompact ? 12 : 18),
          SizedBox(
            height: isCompact ? 50 : 58,
            child: FilledButton.icon(
              onPressed: isConnectionBusy
                  ? null
                  : _isConnected
                  ? _disconnect
                  : _connect,
              icon: Icon(_isConnected ? Icons.link_off : Icons.wifi),
              label: Text(_isConnected ? '断开连接' : '连接电脑'),
              style: FilledButton.styleFrom(
                backgroundColor: const Color(0xff536ba8),
                disabledBackgroundColor: const Color(0xffb9c4d6),
                foregroundColor: Colors.white,
                shape: RoundedRectangleBorder(
                  borderRadius: BorderRadius.circular(28),
                ),
                textStyle: const TextStyle(
                  fontSize: 20,
                  fontWeight: FontWeight.w700,
                ),
              ),
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildTalkButton({required bool canTalk, required double size}) {
    return Listener(
      behavior: HitTestBehavior.opaque,
      onPointerDown: _handleTalkPointerDown,
      onPointerUp: _handleTalkPointerEnd,
      onPointerCancel: _handleTalkPointerEnd,
      child: Center(
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 140),
          width: size,
          height: size,
          decoration: BoxDecoration(
            shape: BoxShape.circle,
            color: _isRecording
                ? const Color(0xffe66b6b)
                : canTalk
                ? const Color(0xff536ba8)
                : const Color(0xffcbd5e6),
            boxShadow: [
              BoxShadow(
                color:
                    (_isRecording
                            ? const Color(0xffe66b6b)
                            : const Color(0xff9fb0ca))
                        .withValues(alpha: 0.22),
                blurRadius: 30,
                offset: const Offset(0, 16),
              ),
            ],
          ),
          alignment: Alignment.center,
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              const Icon(Icons.mic, color: Colors.white, size: 48),
              const SizedBox(height: 10),
              Text(
                _isRecording ? '松开停止' : '按住说话',
                style: const TextStyle(
                  color: Colors.white,
                  fontSize: 24,
                  fontWeight: FontWeight.w900,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _QrScannerPage extends StatefulWidget {
  const _QrScannerPage();

  @override
  State<_QrScannerPage> createState() => _QrScannerPageState();
}

class _QrScannerPageState extends State<_QrScannerPage> {
  final MobileScannerController _controller = MobileScannerController(
    detectionSpeed: DetectionSpeed.noDuplicates,
    formats: [BarcodeFormat.qrCode],
  );
  bool _hasResult = false;

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  void _onDetect(BarcodeCapture capture) {
    if (_hasResult) {
      return;
    }

    for (final barcode in capture.barcodes) {
      final code = barcode.rawValue;
      if (code == null || code.isEmpty) {
        continue;
      }

      _hasResult = true;
      Navigator.of(context).pop(code);
      return;
    }
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: const Color(0xff07101f),
      body: SafeArea(
        child: Stack(
          children: [
            Positioned.fill(
              child: MobileScanner(
                controller: _controller,
                onDetect: _onDetect,
              ),
            ),
            Positioned.fill(
              child: IgnorePointer(
                child: DecoratedBox(
                  decoration: BoxDecoration(
                    gradient: LinearGradient(
                      begin: Alignment.topCenter,
                      end: Alignment.bottomCenter,
                      colors: [
                        Colors.black.withValues(alpha: 0.50),
                        Colors.transparent,
                        Colors.black.withValues(alpha: 0.55),
                      ],
                    ),
                  ),
                ),
              ),
            ),
            Positioned(
              left: 20,
              right: 20,
              top: 20,
              child: Row(
                children: [
                  IconButton.filledTonal(
                    onPressed: () => Navigator.of(context).pop(),
                    icon: const Icon(Icons.close),
                  ),
                  const SizedBox(width: 12),
                  const Expanded(
                    child: Text(
                      '扫描电脑端二维码',
                      style: TextStyle(
                        color: Colors.white,
                        fontSize: 22,
                        fontWeight: FontWeight.w900,
                      ),
                    ),
                  ),
                ],
              ),
            ),
            Center(
              child: Container(
                width: 260,
                height: 260,
                decoration: BoxDecoration(
                  borderRadius: BorderRadius.circular(28),
                  border: Border.all(
                    color: Colors.white.withValues(alpha: 0.90),
                    width: 3,
                  ),
                ),
              ),
            ),
            const Positioned(
              left: 24,
              right: 24,
              bottom: 42,
              child: Text(
                '将电脑端二维码放入框内，识别后会自动连接。',
                textAlign: TextAlign.center,
                style: TextStyle(
                  color: Colors.white,
                  fontSize: 16,
                  fontWeight: FontWeight.w600,
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _SoftCard extends StatelessWidget {
  const _SoftCard({
    required this.child,
    this.padding = const EdgeInsets.all(20),
  });

  final Widget child;
  final EdgeInsetsGeometry padding;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: padding,
      decoration: BoxDecoration(
        color: Colors.white,
        borderRadius: BorderRadius.circular(28),
        border: Border.all(color: const Color(0xffdce8f4)),
        boxShadow: [
          BoxShadow(
            color: const Color(0xff6d88a8).withValues(alpha: 0.10),
            blurRadius: 22,
            offset: const Offset(0, 10),
          ),
        ],
      ),
      child: child,
    );
  }
}

class _SoftTextField extends StatelessWidget {
  const _SoftTextField({
    required this.controller,
    required this.enabled,
    required this.labelText,
    this.hintText,
  });

  final TextEditingController controller;
  final bool enabled;
  final String labelText;
  final String? hintText;

  @override
  Widget build(BuildContext context) {
    return TextField(
      controller: controller,
      enabled: enabled,
      keyboardType: TextInputType.number,
      style: const TextStyle(
        color: Color(0xff10213b),
        fontSize: 18,
        fontWeight: FontWeight.w600,
      ),
      decoration: InputDecoration(
        filled: true,
        fillColor: const Color(0xfff7f9fd),
        labelText: labelText,
        hintText: hintText,
        labelStyle: const TextStyle(
          color: Color(0xff6f7a8f),
          fontSize: 17,
          fontWeight: FontWeight.w500,
        ),
        hintStyle: const TextStyle(
          color: Color(0xff6f7a8f),
          fontSize: 17,
          fontWeight: FontWeight.w500,
        ),
        contentPadding: const EdgeInsets.symmetric(
          horizontal: 16,
          vertical: 19,
        ),
        border: OutlineInputBorder(
          borderRadius: BorderRadius.circular(18),
          borderSide: BorderSide.none,
        ),
        enabledBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(18),
          borderSide: BorderSide.none,
        ),
        focusedBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(18),
          borderSide: const BorderSide(color: Color(0xff88a2d8), width: 1.4),
        ),
        disabledBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(18),
          borderSide: BorderSide.none,
        ),
      ),
    );
  }
}

class _StatusPanel extends StatelessWidget {
  const _StatusPanel({
    required this.status,
    required this.isConnected,
    required this.isConnecting,
    required this.isReconnecting,
    required this.isRecording,
    required this.level,
    required this.onRetry,
  });

  final String status;
  final bool isConnected;
  final bool isConnecting;
  final bool isReconnecting;
  final bool isRecording;
  final double level;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    final statusTitle = isRecording
        ? '正在说话'
        : isReconnecting
        ? '正在重连'
        : isConnecting
        ? '正在连接'
        : isConnected
        ? '已连接'
        : status;
    final isBusy = isConnecting || isReconnecting;
    final statusDescription = isConnected || isReconnecting
        ? '保持手机和电脑在同一个 WiFi 下'
        : '扫描或输入电脑端显示的 IP 和端口';

    return _SoftCard(
      padding: const EdgeInsets.fromLTRB(20, 20, 20, 22),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            crossAxisAlignment: CrossAxisAlignment.center,
            children: [
              Icon(
                isConnected ? Icons.check_circle : Icons.info,
                color: const Color(0xff68748a),
                size: 28,
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Text(
                  statusTitle,
                  maxLines: 2,
                  overflow: TextOverflow.ellipsis,
                  style: const TextStyle(
                    color: Color(0xff10213b),
                    fontSize: 22,
                    fontWeight: FontWeight.w900,
                    height: 1.12,
                  ),
                ),
              ),
              if (isBusy) ...[
                const SizedBox(width: 14),
                _RetryStatusAction(isBusy: true, onRetry: onRetry),
              ] else if (!isConnected) ...[
                const SizedBox(width: 14),
                _RetryStatusAction(isBusy: false, onRetry: onRetry),
              ],
            ],
          ),
          const SizedBox(height: 12),
          Padding(
            padding: const EdgeInsets.only(left: 40),
            child: Text(
              statusDescription,
              style: const TextStyle(
                color: Color(0xff718096),
                fontSize: 16,
                fontWeight: FontWeight.w500,
              ),
            ),
          ),
          const SizedBox(height: 22),
          ClipRRect(
            borderRadius: BorderRadius.circular(999),
            child: LinearProgressIndicator(
              value: isRecording ? level : 0,
              minHeight: 10,
              backgroundColor: const Color(0xffe4ebf7),
              color: const Color(0xff536ba8),
            ),
          ),
        ],
      ),
    );
  }
}

class _RetryStatusAction extends StatelessWidget {
  const _RetryStatusAction({required this.isBusy, required this.onRetry});

  final bool isBusy;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    if (isBusy) {
      return const SizedBox(
        width: 24,
        height: 24,
        child: CircularProgressIndicator(
          strokeWidth: 2.8,
          color: Color(0xff536ba8),
        ),
      );
    }

    return IconButton(
      onPressed: onRetry,
      icon: const SizedBox(
        width: 22,
        height: 22,
        child: CircularProgressIndicator(
          strokeWidth: 2.6,
          color: Color(0xff536ba8),
        ),
      ),
      padding: EdgeInsets.zero,
      constraints: const BoxConstraints(minWidth: 32, minHeight: 32),
      tooltip: '重试连接',
      style: IconButton.styleFrom(
        tapTargetSize: MaterialTapTargetSize.shrinkWrap,
      ),
    );
  }
}
