# MobileToPcInput

Flutter Android 手机麦克风 + Avalonia Windows 接收器。手机通过 WiFi 发送
`16kHz / mono / 16-bit PCM` 音频。电脑端支持三种输入方式：

- C# ONNX Paraformer 本机离线识别。
- 小米 MiMo 在线 ASR。
- 桥接输入：将手机音频送入 VB-CABLE，并触发微信输入法语音输入。

## 参考项目

- C# Paraformer 运行时参考：[FunASR AliParaformerAsr](https://github.com/modelscope/FunASR/blob/main/runtime/csharp/AliParaformerAsr/README.md)

## 项目结构

- `mobile_app`: Flutter Android App，输入电脑 IP/端口，按住说话发送音频。
- `pc_receiver`: Avalonia + .NET 10 Windows 接收器，监听 TCP、调用 C# ONNX Paraformer 并输入文本。

## 使用流程

### 本地/在线识别

1. 使用本地识别时，准备 Paraformer ONNX 模型，默认目录为
   `%USERPROFILE%\.cache\modelscope\hub\models\iic\speech_paraformer-large_asr_nat-zh-cn-16k-common-vocab8404-onnx`。
2. 模型目录需要包含 `model_quant.onnx` 或 `model.onnx`、`config.yaml` 或 `asr.yaml`、`am.mvn`、`tokens.json` 或 `tokens.txt`。
3. 启动 `pc_receiver`，点击“开始监听”，记下窗口里的电脑 IP 和端口，默认端口是 `8765`。
4. 在 `mobile_app` 里输入电脑 IP 和端口，点击“连接电脑”。
5. 手机端按住“按住说话”，PC 端完成离线识别后会把文字输入到当前光标位置。

### 桥接输入（微信输入法）

首次使用需要完成以下设置：

1. 安装并启用微信输入法。切换到桥接输入时，如果程序没有检测到
   VB-Audio Virtual Cable，可在提示窗口中直接启动内嵌的官方安装程序。
2. 在 Windows“系统 → 声音 → 输入”中，将
   `CABLE Output (VB-Audio Virtual Cable)`设为默认录音设备。
3. 启动 `pc_receiver`，将“识别方式”切换为“桥接输入”，确认桥接输出为
   `CABLE Input (VB-Audio Virtual Cable)`，然后开始监听。
4. 在右侧“按住说话快捷键”中点击当前组合，然后同时按下需要录制的按键；
   默认是 `Ctrl + Win`，松开任意按键后自动保存。
5. 在微信输入法“语音输入 → 快捷键”中设置相同的“按住说话”组合。
6. 在任意输入框中启用微信输入法。手机按住说话时，电脑端会保持已配置组合并
   转发手机音频；松开后排空音频并按相反顺序释放快捷键。

如果未检测到 VB-CABLE，或默认麦克风不是 `CABLE Output`，电脑端会阻止启动并显示对应提示。

## 开发命令

```powershell
cd D:\Workspace\Test\MobileToPcInput\pc_receiver
dotnet build
dotnet run
```

PC 端框架依赖单文件发布和 MSI 打包：

```powershell
cd D:\Workspace\Test\MobileToPcInput
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
```

该脚本会同时构建 Android Release APK 和 PC MSI，并检查两端版本号一致。
最终文件统一输出到仓库根目录的 `publish` 文件夹。

发布产物不包含 .NET 运行时。运行 PC 端前，目标电脑需要安装与项目版本匹配的
`.NET 10 Runtime x64`。

### PC 引导安装程序

MSI 和 Burn 引导安装程序本身只安装 MobileToPcInput。PC 程序内嵌了
VB-CABLE 官方原始 ZIP 包；当前使用桥接输入，或用户切换到桥接输入时，
程序会检测 `CABLE Input`。未检测到时会先征得用户同意，再把内嵌包解压到
独立临时目录，以管理员权限启动 `VBCABLE_Setup_x64.exe`。官方安装程序结束后，
临时目录会被清理；用户需要重启 Windows，然后在程序中刷新桥接输出。

```powershell
cd D:\Workspace\Test\MobileToPcInput
powershell -ExecutionPolicy Bypass -File .\pc_receiver\scripts\build-setup.ps1
```

VB-CABLE 来源于 [www.vb-cable.com](https://www.vb-cable.com)，是 VB-Audio
提供的第三方 Donationware 驱动，欢迎用户按实际使用情况自愿支持作者。
专业、机构或批量分发请遵守
[VB-Audio Licensing](https://vb-audio.com/Services/licensing.htm)。

安装包输出到：

```text
D:\Workspace\Test\MobileToPcInput\publish\MobileToPcInput-Android-1.0.2.apk
D:\Workspace\Test\MobileToPcInput\publish\MobileToPcInput-1.0.2-x64.msi
D:\Workspace\Test\MobileToPcInput\publish\MobileToPcInput-Setup-1.0.2-x64.exe
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
  -> 本地/在线 ASR -> TextInputService
     或
  -> CABLE Input -> CABLE Output -> 微信输入法语音输入
  -> 当前光标位置
```

<img width="500" height="350" alt="image" src="https://github.com/user-attachments/assets/fb24e223-be70-48c1-b3dc-91e4cb7d0aa5" />
<br>
<img width="350" height="750" alt="Screenshot_2026-07-15-22-31-25-592_com yarkool mobiletopcinput mobile_app" src="https://github.com/user-attachments/assets/1d31e15d-6513-4be6-af72-8e43529396ff" />



