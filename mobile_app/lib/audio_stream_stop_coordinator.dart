import 'dart:async';

Future<void> finishAudioStream({
  required Future<void> Function() stopRecorder,
  required Future<void> streamDone,
  required Future<void> Function() cancelSubscription,
  required Future<void> Function() flushSocket,
  Duration timeout = const Duration(seconds: 3),
}) async {
  await stopRecorder();
  try {
    await streamDone.timeout(timeout);
  } on TimeoutException {
    // Avoid hanging forever when a device fails to close its recorder stream.
  }
  await cancelSubscription();
  await flushSocket();
}
