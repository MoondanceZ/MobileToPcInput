// This is a basic Flutter widget test.
//
// To perform an interaction with a widget in your test, use the WidgetTester
// utility in the flutter_test package. For example, you can send tap and scroll
// gestures. You can also use WidgetTester to find child widgets in the widget
// tree, read text, and verify that the values of widget properties are correct.

import 'package:flutter_test/flutter_test.dart';

import 'package:mobile_app/main.dart';

void main() {
  testWidgets('shows receiver form and push-to-talk button', (tester) async {
    await tester.pumpWidget(const MobileToPcInputApp());

    expect(find.text('手机麦克风'), findsOneWidget);
    expect(find.text('电脑接收器'), findsOneWidget);
    expect(find.text('电脑 IP'), findsOneWidget);
    expect(find.text('按住说话'), findsOneWidget);
  });
}
