import 'dart:async';

Future<void> finishAudioStream({
  required Future<void> Function() stopRecorder,
  required Future<void> streamDone,
  required Future<void> Function() cancelSubscription,
  required Future<void> Function() flushSocket,
  Duration timeout = const Duration(seconds: 3),
  void Function(Object error)? onError,
}) async {
  var recorderStopped = true;
  try {
    await stopRecorder();
  } catch (error) {
    recorderStopped = false;
    onError?.call(error);
  }

  if (recorderStopped) {
    try {
      await streamDone.timeout(timeout);
    } on TimeoutException {
      // Avoid hanging forever when a device fails to close its recorder stream.
    } catch (error) {
      onError?.call(error);
    }
  }

  try {
    await cancelSubscription();
  } catch (error) {
    onError?.call(error);
  }

  try {
    await flushSocket();
  } catch (error) {
    onError?.call(error);
  }
}
