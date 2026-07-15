# MobileToPcInput

Flutter Android 手机麦克风 + Avalonia Windows 接收器。手机通过 WiFi 发送
`16kHz / mono / 16-bit PCM` 音频，电脑端使用 C# ONNX Paraformer 本机离线识别，
并把识别文本直接输入到当前光标位置。

## 参考项目

- C# Paraformer 运行时参考：[FunASR AliParaformerAsr](https://github.com/modelscope/FunASR/blob/main/runtime/csharp/AliParaformerAsr/README.md)

## 项目结构

- `mobile_app`: Flutter Android App，输入电脑 IP/端口，按住说话发送音频。
- `pc_receiver`: Avalonia + .NET 10 Windows 接收器，监听 TCP、调用 C# ONNX Paraformer 并输入文本。

## 使用流程

1. 准备 Paraformer ONNX 模型，默认目录为
   `%USERPROFILE%\.cache\modelscope\hub\models\iic\speech_paraformer-large_asr_nat-zh-cn-16k-common-vocab8404-onnx`。
2. 模型目录需要包含 `model_quant.onnx` 或 `model.onnx`、`config.yaml` 或 `asr.yaml`、`am.mvn`、`tokens.json` 或 `tokens.txt`。
3. 启动 `pc_receiver`，点击“开始监听”，记下窗口里的电脑 IP 和端口，默认端口是 `8765`。
4. 在 `mobile_app` 里输入电脑 IP 和端口，点击“连接电脑”。
5. 手机端按住“按住说话”，PC 端完成离线识别后会把文字输入到当前光标位置。

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
  -> C# ONNX Paraformer
  -> TextInputService
  -> 当前光标位置
```

<img width="500" height="350" alt="image" src="https://github.com/user-attachments/assets/fb24e223-be70-48c1-b3dc-91e4cb7d0aa5" />
<br>
<img width="350" height="750" alt="Screenshot_2026-07-15-22-31-25-592_com yarkool mobiletopcinput mobile_app" src="https://github.com/user-attachments/assets/1d31e15d-6513-4be6-af72-8e43529396ff" />



