# MobileToPcInput

Flutter Android 手机麦克风 + Avalonia Windows 接收器。手机通过 WiFi 发送
`16kHz / mono / 16-bit PCM` 音频，电脑端把音频播放到虚拟音频线的播放设备，
再由 VocoType 从对应录音设备读取。

## 项目结构

- `mobile_app`: Flutter Android App，输入电脑 IP/端口，按住说话发送音频。
- `pc_receiver`: Avalonia + .NET 10 Windows 接收器，监听 TCP 并输出音频。

## 使用流程

1. 安装并启用 VB-CABLE 或 Voicemeeter 这类虚拟音频线。
2. 启动 `pc_receiver`，输出设备选择 `CABLE Input` 或类似播放端。
3. 点击“开始监听”，记下窗口里的电脑 IP 和端口，默认端口是 `8765`。
4. 在 `mobile_app` 里输入电脑 IP 和端口，点击“连接电脑”。
5. VocoType 的录音输入选择 `CABLE Output` 或对应录音端。
6. 手机端按住“按住说话”，VocoType 应该能收到语音。

## 开发命令

```powershell
cd D:\Workspace\Test\MobileToPcInput\pc_receiver
dotnet build
dotnet run
```

PC 端 AOT 发布和 MSI 打包：

```powershell
cd D:\Workspace\Test\MobileToPcInput\pc_receiver
dotnet publish .\pc_receiver.csproj -c Release -r win-x64
powershell -ExecutionPolicy Bypass -File .\scripts\build-msi.ps1
```

安装包输出到：

```text
D:\Workspace\Test\MobileToPcInput\pc_receiver\artifacts\MobileToPcInput-1.0.0-x64.msi
```

```powershell
cd D:\Workspace\Test\MobileToPcInput\mobile_app
flutter pub get
flutter analyze
flutter run
```

如果 Flutter Android licenses 未接受，先运行：

```powershell
flutter doctor --android-licenses
```

## 端到端链路

```text
Android microphone
  -> Flutter record PCM stream
  -> TCP 电脑IP:8765
  -> Avalonia receiver
  -> NAudio WaveOut output device
  -> VB-CABLE playback side
  -> VB-CABLE recording side
  -> VocoType
```
