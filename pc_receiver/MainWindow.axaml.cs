using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace pc_receiver;

public partial class MainWindow : Window
{
    private static readonly RecognitionModeOption[] RecognitionModeOptions =
    [
        new(RecognitionModes.Local, "本地模型"),
        new(RecognitionModes.Online, "在线服务"),
        new(RecognitionModes.WeType, "桥接输入"),
    ];

    private readonly AudioOutputService _audioOutput = new();
    private readonly WeTypeHotkeyService _weTypeHotkey;
    private readonly WeTypeBridgeSession _weTypeBridge;
    private readonly AudioReceiverServer _server = new();
    private readonly AsrSessionBuffer _asrBuffer = new();
    private readonly ParaformerAsrService _asrService = new();
    private readonly XiaomiMimoAsrService _xiaomiMimoAsrService = new();
    private readonly ModelDownloadService _modelDownloadService = new();
    private readonly AppSettingsService _settingsService = new();
    private readonly StartupService _startupService = new();
    private readonly TrayIcon _trayIcon;
    private readonly NativeMenuItem _trayStartItem;
    private readonly NativeMenuItem _trayStopItem;
    private readonly NativeMenuItem _trayStartupItem;
    private const double TopDragHeight = 156;
    private bool _allowClose;
    private bool _isRecognizing;
    private bool _isAsrReady;
    private bool _isModelOperationRunning;
    private bool _isRefreshingModels;
    private bool _isRefreshingMode;
    private bool _isApplyingSelectedModel;
    private bool _isCapturingHotkey;
    private readonly List<string> _capturedHotkeyTokens = [];
    private AppSettings _settings = new();
    private string _modelOperationMessage = "切换模型后会重新加载识别引擎；模型文件保存在 ModelScope 本地缓存中。";
    private double _modelOperationProgress;
    private bool _modelOperationIsIndeterminate;

    private event Action? ModelOperationChanged;

    public MainWindow()
    {
        WindowDecorations = WindowDecorations.None;
        CanResize = false;
        Topmost = false;
        ExtendClientAreaToDecorationsHint = true;
        TransparencyLevelHint =
        [
            WindowTransparencyLevel.Transparent,
            WindowTransparencyLevel.AcrylicBlur,
            WindowTransparencyLevel.None
        ];
        Background = Brushes.Transparent;
        _settings = _settingsService.Load();
        _settings.RecognitionMode = RecognitionModes.Normalize(_settings.RecognitionMode);
        _weTypeHotkey = new WeTypeHotkeyService();
        _weTypeHotkey.SetHotkey(
            BridgeHotkeyDefinition.Parse(_settings.BridgeHotkey)
                .ForSession(_settings.BridgeHotkeyEnabled));
        _weTypeBridge = new WeTypeBridgeSession(_audioOutput, _weTypeHotkey);
        InitializeComponent();
        Surface.AddHandler(PointerPressedEvent, DragWindowFromTopArea, RoutingStrategies.Tunnel);
        TitleBar.AddHandler(PointerPressedEvent, DragWindow, RoutingStrategies.Tunnel);
        SetAppImages();
        PortBox.Text = IsValidPort(_settings.Port) ? _settings.Port.ToString() : "8765";
        var startupEnabled = _startupService.IsEnabled();
        StartupBox.IsChecked = startupEnabled;
        ReplaceTrailingFullStopBox.IsChecked = _settings.ReplaceTrailingFullStopWithSpace;
        BridgeHotkeyEnabledBox.IsChecked = _settings.BridgeHotkeyEnabled;
        _asrService.ReplaceTrailingFullStopWithSpaceEnabled = _settings.ReplaceTrailingFullStopWithSpace;
        _settings.StartupEnabled = startupEnabled;
        SaveSettings();
        StartupBox.Click += (_, _) => SetStartupEnabled(StartupBox.IsChecked == true);
        ReplaceTrailingFullStopBox.Click += (_, _) =>
            SetReplaceTrailingFullStopWithSpace(ReplaceTrailingFullStopBox.IsChecked == true);
        BridgeHotkeyEnabledBox.Click += (_, _) =>
            SetBridgeHotkeyEnabled(BridgeHotkeyEnabledBox.IsChecked == true);
        HotkeyCaptureButton.Click += (_, _) => StartHotkeyCapture();
        AddHandler(KeyDownEvent, CaptureHotkeyKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, CaptureHotkeyKeyUp, RoutingStrategies.Tunnel);
        ModeBox.ItemsSource = RecognitionModeOptions;
        RefreshRecognitionModePicker();
        ModeBox.SelectionChanged += async (_, _) => await ApplyRecognitionModeAsync();
        DeviceBox.SelectionChanged += async (_, _) => await ApplySelectedModelAsync();
        PortBox.TextChanged += (_, _) =>
        {
            UpdateConnectQrCode();
            SavePortSetting();
        };
        _asrService.WorkerStatusChanged += OnAsrWorkerStatus;

        _trayStartItem = new NativeMenuItem("开始监听");
        _trayStopItem = new NativeMenuItem("停止监听") { IsEnabled = false };
        _trayStartupItem = new NativeMenuItem("开机自动启动")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = StartupBox.IsChecked == true
        };
        _trayIcon = CreateTrayIcon();

        IpText.Text = NetworkAddressHelper.GetPreferredLocalIp();
        RefreshModels();
        UpdateConnectQrCode();
        ClearAudioCache(showStatus: false);
        Loaded += (_, _) => _ = WarmUpAsrOnStartupAsync();

        _server.ClientStateChanged += connected =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                ClientText.Text = connected ? "手机已连接" : "手机未连接";
                if (connected)
                {
                    SetStatus("● 手机已连接，等待音频", "#1769E0", "#EEF6FF");
                }
                else if (StopButton.IsEnabled)
                {
                    SetStatus("● 正在监听", "#1769E0", "#EEF6FF");
                }

                if (!connected)
                {
                    LevelBar.Value = 0;
                    if (IsWeTypeRecognitionSelected())
                    {
                        _weTypeBridge.Abort();
                        UpdateHotkeyButtonsEnabled();
                    }
                    else if (_asrBuffer.IsRecording)
                    {
                        _ = FinishAsrSessionAsync();
                    }
                }
            });
        };
        _server.AudioFrameReceived += bytes =>
        {
            try
            {
                if (IsWeTypeRecognitionSelected())
                {
                    _weTypeBridge.AddAudio(bytes);
                }
                else
                {
                    _asrBuffer.AddSamples(bytes);
                }

                var level = AudioLevelMeter.CalculatePercent(bytes);
                Dispatcher.UIThread.Post(() => LevelBar.Value = level);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Audio output failed", ex);
                Dispatcher.UIThread.Post(() => StatusText.Text = $"音频输出失败: {ex.Message}");
            }
        };
        _server.ControlMessageReceived += HandleControlMessageAsync;
        _server.StatusChanged += message =>
        {
            Dispatcher.UIThread.Post(() => StatusText.Text = message);
        };
    }

    private async Task HandleControlMessageAsync(string message)
    {
        try
        {
            using var document = JsonDocument.Parse(message);
            if (!document.RootElement.TryGetProperty("type", out var type))
            {
                return;
            }

            var control = type.GetString();
            if (IsStartControl(control))
            {
                if (IsWeTypeRecognitionSelected())
                {
                    if (_isCapturingHotkey)
                    {
                        Dispatcher.UIThread.Post(
                            () => StatusText.Text = "正在设置快捷键，请完成或按 Esc 取消");
                        return;
                    }

                    await _weTypeBridge.StartAsync();
                    AppLogger.Info($"WeType bridge session started by control: {control}");
                    Dispatcher.UIThread.Post(() =>
                    {
                        StatusText.Text = _settings.BridgeHotkeyEnabled
                            ? "正在调用输入法语音输入，松开后结束"
                            : "正在桥接手机音频，松开后结束";
                        UpdateHotkeyButtonsEnabled();
                    });
                    return;
                }

                if (!_isAsrReady)
                {
                    AppLogger.Info($"ASR start rejected because worker is not ready. control={control}");
                    Dispatcher.UIThread.Post(() => StatusText.Text = "语音模型加载中，请稍后再说话");
                    return;
                }

                AppLogger.Info($"ASR session started by control: {control}");
                _asrBuffer.Start();
                Dispatcher.UIThread.Post(() => StatusText.Text = "正在录音，松开后识别");
                return;
            }

            if (!IsStopControl(control))
            {
                return;
            }

            if (IsWeTypeRecognitionSelected())
            {
                await _weTypeBridge.StopAsync();
                AppLogger.Info($"WeType bridge session stopped by control: {control}");
                Dispatcher.UIThread.Post(() =>
                {
                    StatusText.Text = _settings.BridgeHotkeyEnabled
                        ? "输入法语音输入已结束"
                        : "手机音频桥接已结束";
                    LevelBar.Value = 0;
                    UpdateHotkeyButtonsEnabled();
                });
                return;
            }

            if (!_asrBuffer.IsRecording)
            {
                AppLogger.Info($"ASR stop ignored because no recording session is active. control={control}");
                return;
            }

            AppLogger.Info($"ASR session stopping by control: {control}");
            await FinishAsrSessionAsync();
        }
        catch (Exception ex)
        {
            _weTypeBridge.Abort();
            AppLogger.Error("Control message failed", ex);
            Dispatcher.UIThread.Post(() => StatusText.Text = $"语音控制失败: {ex.Message}");
        }
    }

    private async void StartButton_Click(object? sender, RoutedEventArgs e)
    {
        await StartListeningAsync();
    }

    private async Task StartListeningAsync()
    {
        if (!int.TryParse(PortBox.Text, out var port) || port <= 0 || port > 65535)
        {
            StatusText.Text = "端口不正确";
            return;
        }

        if (!await EnsureSelectedModelReadyAsync())
        {
            return;
        }

        try
        {
            await _server.StartAsync(port);
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            PortBox.IsEnabled = false;
            ModeBox.IsEnabled = false;
            DeviceBox.IsEnabled = false;
            ManageModelButton.IsEnabled = !IsWeTypeRecognitionSelected();
            UpdateHotkeyButtonsEnabled();
            SetStatus($"● 正在监听 0.0.0.0:{port}", "#1769E0", "#EEF6FF");
            SyncListeningUi(isListening: true);
        }
        catch (Exception ex)
        {
            if (IsWeTypeRecognitionSelected())
            {
                _weTypeBridge.Abort();
                _audioOutput.Stop();
            }

            SetStatus($"● 启动失败: {ex.Message}", "#C13830", "#FFF1F0");
            SyncListeningUi(isListening: false);
            PortBox.IsEnabled = true;
            ModeBox.IsEnabled = true;
            DeviceBox.IsEnabled = true;
            ManageModelButton.IsEnabled = !IsWeTypeRecognitionSelected();
            UpdateHotkeyButtonsEnabled();
        }
    }

    private void StopButton_Click(object? sender, RoutedEventArgs e)
    {
        StopServer();
    }

    private void RefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        RefreshModels();
    }

    private void RefreshModels()
    {
        var selectedId = GetSelectedModel()?.Id ?? GetConfiguredModelId() ?? _asrService.CurrentModel.Id;
        _isRefreshingModels = true;
        try
        {
            DeviceBox.ItemsSource = null;
            if (IsWeTypeRecognitionSelected())
            {
                var devices = _audioOutput.GetDevices().Where(item => item.IsLikelyVirtualCable).ToArray();
                DeviceBox.ItemsSource = devices;
                DeviceBox.SelectedItem = devices.FirstOrDefault(
                    item => string.Equals(
                        item.Name,
                        _settings.WeTypeOutputDeviceName,
                        StringComparison.OrdinalIgnoreCase))
                    ?? devices.FirstOrDefault();
            }
            else
            {
                DeviceBox.ItemsSource = IsOnlineRecognitionSelected()
                    ? new object[] { OnlineAsrCatalog.DefaultService }
                    : AsrModelCatalog.Models.Cast<object>().ToArray();
                DeviceBox.SelectedItem = IsOnlineRecognitionSelected()
                    ? OnlineAsrCatalog.DefaultService
                    : AsrModelCatalog.Models.FirstOrDefault(model => model.Id == selectedId)
                        ?? AsrModelCatalog.DefaultModel;
            }

            UpdateModelUi();
        }
        finally
        {
            _isRefreshingModels = false;
        }
    }

    private void StopServer()
    {
        _server.Stop();
        _weTypeBridge.Abort();
        _audioOutput.Stop();
        SyncListeningUi(isListening: false);
        PortBox.IsEnabled = true;
        ModeBox.IsEnabled = true;
        DeviceBox.IsEnabled = true;
        ManageModelButton.IsEnabled = !IsWeTypeRecognitionSelected();
        UpdateHotkeyButtonsEnabled();
        UpdateModelUi();
        SetStatus("● 未监听", "#C13830", "#FFF1F0");
        ClientText.Text = "手机未连接";
        LevelBar.Value = 0;
    }

    private async void ManageModelButton_Click(object? sender, RoutedEventArgs e)
    {
        var window = new ModelManagerWindow(
            LoadModelFromManagerAsync,
            DeleteModelFromManagerAsync,
            RefreshModels,
            () => _isAsrReady ? _asrService.CurrentModel.Id : null,
            GetModelOperationSnapshot,
            handler => ModelOperationChanged += handler,
            handler => ModelOperationChanged -= handler,
            () => _settings,
            SaveXiaomiSettings,
            SwitchRecognitionModeFromManagerAsync,
            GetActiveOnlineServiceId);
        ShowDialogBackdrop();
        try
        {
            await window.ShowDialog(this);
        }
        finally
        {
            HideDialogBackdrop();
            RefreshModels();
        }
    }

    private async Task LoadModelFromManagerAsync(AsrModelOption model)
    {
        if (!model.IsSupported)
        {
            throw new NotSupportedException("这个模型入口已预留，当前版本暂不支持");
        }

        if (_asrBuffer.IsRecording || _isRecognizing)
        {
            throw new InvalidOperationException("正在录音或识别中，完成后再切换模型");
        }

        var wasDownloaded = model.IsDownloaded && model.IsPunctuationDownloaded && model.IsVadDownloaded;
        var shouldLoadAfterDownload = wasDownloaded || !_isAsrReady;
        if (shouldLoadAfterDownload)
        {
            DeviceBox.SelectedItem = model;
        }

        SetModelOperation(
            isRunning: true,
            message: wasDownloaded ? "正在加载模型..." : "正在准备模型...",
            progress: wasDownloaded ? 35 : 2,
            isIndeterminate: !wasDownloaded);
        try
        {
            if (!wasDownloaded)
            {
                var progress = new ActionProgress<ModelDownloadProgress>(item =>
                {
                    SetModelOperation(
                        isRunning: true,
                        message: item.Message,
                        progress: Math.Min(item.Progress, 70),
                        isIndeterminate: item.IsIndeterminate);
                });
                await _modelDownloadService.DownloadRequiredModelsAsync(model, progress);
                RefreshModels();

                if (!shouldLoadAfterDownload)
                {
                    SetModelOperation(
                        isRunning: false,
                        message: $"已下载 {model.DisplayName}",
                        progress: 100,
                        isIndeterminate: false);
                    return;
                }
            }

            DeviceBox.SelectedItem = model;
            SetModelOperation(
                isRunning: true,
                message: "正在加载模型...",
                progress: 72,
                isIndeterminate: false);
            await WarmUpAsrAsync(model, startListeningWhenReady: true);
            SaveSelectedModelSetting(model);
            SetModelOperation(
                isRunning: false,
                message: _isAsrReady && _asrService.CurrentModel.Id == model.Id
                    ? $"已加载 {model.DisplayName}"
                    : $"加载失败: {model.DisplayName}",
                progress: _isAsrReady && _asrService.CurrentModel.Id == model.Id ? 100 : 0,
                isIndeterminate: false);
        }
        catch (Exception ex)
        {
            SetModelOperation(
                isRunning: false,
                message: $"加载失败: {ex.Message}",
                progress: 0,
                isIndeterminate: false);
            throw;
        }

        RefreshModels();
    }

    private async Task DeleteModelFromManagerAsync(AsrModelOption model)
    {
        if (!model.IsDownloaded)
        {
            StatusText.Text = "当前模型尚未下载";
            return;
        }

        if (_asrBuffer.IsRecording || _isRecognizing)
        {
            throw new InvalidOperationException("正在录音或识别中，完成后再删除模型");
        }

        if (_isAsrReady && _asrService.CurrentModel.Id == model.Id)
        {
            throw new InvalidOperationException("当前已加载的模型不能删除，请先切换到其他模型");
        }

        SetModelOperation(
            isRunning: true,
            message: $"正在删除 {model.DisplayName}...",
            progress: 30,
            isIndeterminate: false);
        try
        {
            var deleted = AsrModelCatalog.DeleteModelFiles(model);
            AppLogger.Info($"ASR model cache deleted. model={model.Id}, directories={deleted}");
            SetStatus($"● 已删除模型缓存: {model.DisplayName}", "#C13830", "#FFF1F0");
            SetModelOperation(
                isRunning: false,
                message: $"已删除 {model.DisplayName}",
                progress: 100,
                isIndeterminate: false);
        }
        catch (Exception ex)
        {
            SetModelOperation(
                isRunning: false,
                message: $"删除失败: {ex.Message}",
                progress: 0,
                isIndeterminate: false);
            throw;
        }

        RefreshModels();
    }

    private ModelOperationSnapshot GetModelOperationSnapshot()
    {
        return new ModelOperationSnapshot(
            _isModelOperationRunning,
            _modelOperationMessage,
            _modelOperationProgress,
            _modelOperationIsIndeterminate);
    }

    private void SetModelOperation(bool isRunning, string message, double progress, bool isIndeterminate)
    {
        void Apply()
        {
            _isModelOperationRunning = isRunning;
            _modelOperationMessage = message;
            _modelOperationProgress = Math.Clamp(progress, 0, 100);
            _modelOperationIsIndeterminate = isIndeterminate;
            ModelOperationChanged?.Invoke();
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Apply();
            return;
        }

        Dispatcher.UIThread.Post(Apply);
    }

    private void OnAsrWorkerStatus(string message)
    {
        var progress = _modelOperationProgress;
        var text = _modelOperationMessage;
        var statusText = string.Empty;
        if (message.Contains("loading C# ONNX model", StringComparison.OrdinalIgnoreCase))
        {
            progress = Math.Max(progress, 55);
            text = "正在加载模型...";
            statusText = "正在加载模型...";
        }
        else if (message.Contains("C# ONNX model ready", StringComparison.OrdinalIgnoreCase))
        {
            progress = Math.Max(progress, 70);
            text = "正在加载模型...";
            statusText = "正在加载模型...";
        }
        else if (message.Contains("loading punctuation model", StringComparison.OrdinalIgnoreCase))
        {
            progress = Math.Max(progress, 82);
            text = "正在加载模型...";
            statusText = "正在加载模型...";
        }
        else if (message.Contains("punctuation model ready", StringComparison.OrdinalIgnoreCase))
        {
            progress = Math.Max(progress, 98);
            text = "模型已就绪...";
            statusText = "模型已就绪";
        }
        else if (message.Contains("C# ONNX recognition starting", StringComparison.OrdinalIgnoreCase))
        {
            progress = Math.Max(progress, 65);
            text = "正在识别语音...";
            statusText = "正在识别语音...";
        }
        else
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!string.IsNullOrWhiteSpace(statusText))
            {
                StatusText.Text = statusText;
            }

            if (_isModelOperationRunning)
            {
                SetModelOperation(isRunning: true, message: text, progress, isIndeterminate: false);
            }
        });
    }

    private AsrModelOption? GetSelectedModel()
    {
        return DeviceBox.SelectedItem as AsrModelOption;
    }

    private AsrModelOption? GetSelectedLocalModel()
    {
        return DeviceBox.SelectedItem as AsrModelOption
            ?? AsrModelCatalog.Models.FirstOrDefault(item => item.Id == _settings.SelectedModelId)
            ?? AsrModelCatalog.DefaultModel;
    }

    private void UpdateModelUi()
    {
        if (IsWeTypeRecognitionSelected())
        {
            ModelLabel.Text = "桥接输出";
            TitleSubtitleText.Text = "手机麦克风接收器 · 桥接输入";
            var hotkey = BridgeHotkeyDefinition.Parse(_settings.BridgeHotkey);
            UsageText.Text = _settings.BridgeHotkeyEnabled
                ? $"使用流程：将 CABLE Output 设为 Windows 默认麦克风；输入法按住说话快捷键设为 {hotkey.DisplayName}；手机连接后按住说话。"
                : "使用流程：将 CABLE Output 设为 Windows 默认麦克风；当前未启用自动按键，手机按住说话时仅转发音频。";
            ClearAudioCacheButton.IsVisible = false;
            ManageModelButton.IsEnabled = false;
            ReplaceTrailingFullStopSettingsCard.IsVisible = false;
            BridgeHotkeySettingsCard.IsVisible = true;
            BridgeHotkeyEnabledBox.IsChecked = _settings.BridgeHotkeyEnabled;
            UpdateHotkeyButtonsEnabled();
            if (!_isCapturingHotkey)
            {
                HotkeyCaptureButton.Content = hotkey.DisplayName;
                HotkeyCaptureHint.Text = "点击右侧按键，可录入一个或多个按键；Esc 取消";
            }

            var device = DeviceBox.SelectedItem as AudioOutputDevice;
            if (device is null)
            {
                HintText.Text = "未检测到 VB-CABLE 的 CABLE Input，请先安装或启用虚拟音频设备。";
                return;
            }

            var defaultCapture = _audioOutput.GetDefaultCaptureDeviceName();
            HintText.Text = !IsCableOutputCapture(defaultCapture)
                ? $"已选择 {device.Name}；请先把 CABLE Output 设为 Windows 默认录音设备。"
                : _settings.BridgeHotkeyEnabled
                    ? $"桥接输出：{device.Name}。按住手机按钮会触发快捷键 {hotkey.DisplayName}。"
                    : $"桥接输出：{device.Name}。自动按键已关闭，仅转发手机音频。";
            return;
        }

        CancelHotkeyCapture();
        ModelLabel.Text = "语音模型";
        TitleSubtitleText.Text = IsOnlineRecognitionSelected()
            ? "手机麦克风接收器 · 在线语音识别"
            : "手机麦克风接收器 · 本地语音识别";
        UsageText.Text = "使用流程：电脑端开始监听；手机连接后按住说话，松开后完成识别，并把文本直接输入到当前输入框。";
        ClearAudioCacheButton.IsVisible = true;
        ManageModelButton.IsEnabled = true;
        ReplaceTrailingFullStopSettingsCard.IsVisible = true;
        BridgeHotkeySettingsCard.IsVisible = false;

        if (IsOnlineRecognitionSelected())
        {
            HintText.Text = $"当前使用：{OnlineAsrCatalog.DefaultService.DisplayName} · {GetLanguageDisplayName(_settings.XiaomiMimoLanguage)}。";
            return;
        }

        var model = GetSelectedLocalModel();
        if (model is null)
        {
            return;
        }

        if (!model.IsSupported)
        {
            HintText.Text = "这个模型入口已预留，当前版本暂不支持。";
            return;
        }

        HintText.Text = model.IsDownloaded
            ? $"当前选择：{model.DisplayName}。"
            : $"当前选择：{model.DisplayName}，模型未下载，请先打开“模型管理”下载。";
    }

    private async Task ApplySelectedModelAsync()
    {
        if (_isRefreshingModels || _isApplyingSelectedModel)
        {
            UpdateModelUi();
            return;
        }

        if (IsWeTypeRecognitionSelected())
        {
            if (DeviceBox.SelectedItem is AudioOutputDevice outputDevice)
            {
                _settings.WeTypeOutputDeviceName = outputDevice.Name;
                SaveSettings();
            }

            UpdateModelUi();
            return;
        }

        var model = GetSelectedLocalModel();
        if (model is null)
        {
            return;
        }

        UpdateModelUi();
        if (!IsOnlineRecognitionSelected() && model.Id == _asrService.CurrentModel.Id)
        {
            return;
        }

        _isApplyingSelectedModel = true;
        try
        {
            if (!model.IsSupported)
            {
                _isAsrReady = false;
                await _asrService.StopWorkerAsync();
                SetStatus("● 当前模型暂不支持", "#C13830", "#FFF1F0");
                return;
            }

            if (!model.IsDownloaded)
            {
                _isAsrReady = false;
                await _asrService.StopWorkerAsync();
                SetStatus("● 模型未下载，请先打开模型管理", "#C13830", "#FFF1F0");
                return;
            }

            await WarmUpAsrAsync(model, startListeningWhenReady: true);
            SaveSelectedModelSetting(model);
        }
        finally
        {
            _isApplyingSelectedModel = false;
        }
    }

    private async Task<bool> EnsureSelectedModelReadyAsync()
    {
        if (IsWeTypeRecognitionSelected())
        {
            var device = DeviceBox.SelectedItem as AudioOutputDevice
                ?? _audioOutput.FindDevice(_settings.WeTypeOutputDeviceName);
            if (device is null)
            {
                StatusText.Text = "未检测到 CABLE Input，请先安装或启用 VB-CABLE";
                return false;
            }

            var defaultCapture = _audioOutput.GetDefaultCaptureDeviceName();
            if (!IsCableOutputCapture(defaultCapture))
            {
                StatusText.Text = "请先把 CABLE Output 设为 Windows 默认录音设备";
                return false;
            }

            try
            {
                _audioOutput.Start(device);
                _settings.WeTypeOutputDeviceName = device.Name;
                SaveSettings();
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("WeType virtual audio output failed to start", ex);
                StatusText.Text = $"虚拟音频输出启动失败: {ex.Message}";
                return false;
            }
        }

        if (IsOnlineRecognitionSelected())
        {
            if (string.IsNullOrWhiteSpace(_settings.XiaomiMimoApiKey))
            {
                StatusText.Text = "请先在模型管理中配置小米 MiMo API Key";
                return false;
            }

            _isAsrReady = true;
            return true;
        }

        var model = GetSelectedLocalModel();
        if (model is null)
        {
            StatusText.Text = "请选择语音模型";
            return false;
        }

        if (!model.IsSupported)
        {
            _isAsrReady = false;
            StatusText.Text = "当前选择的模型暂不支持";
            return false;
        }

        if (!model.IsDownloaded)
        {
            _isAsrReady = false;
            StatusText.Text = "当前选择的模型未下载，请先打开模型管理";
            return false;
        }

        if (!_isAsrReady || _asrService.CurrentModel.Id != model.Id)
        {
            await WarmUpAsrAsync(model);
        }

        return _isAsrReady && _asrService.CurrentModel.Id == model.Id;
    }

    private void SetAppImages()
    {
        Icon = LoadWindowIcon();
        LogoImage.Source = new Bitmap(AssetLoader.Open(new Uri("avares://MobileToPcInput/Assets/app.png")));
    }

    private TrayIcon CreateTrayIcon()
    {
        var showItem = new NativeMenuItem("显示窗口");
        showItem.Click += (_, _) => RestoreWindow();
        _trayStartItem.Click += (_, _) => StartButton_Click(null, new RoutedEventArgs());
        _trayStopItem.Click += (_, _) => StopButton_Click(null, new RoutedEventArgs());
        _trayStartupItem.Click += (_, _) => SetStartupEnabled(_trayStartupItem.IsChecked);

        var exitItem = new NativeMenuItem("退出");
        exitItem.Click += (_, _) => ExitApplication();

        var menu = new NativeMenu
        {
            Items =
            {
                showItem,
                new NativeMenuItemSeparator(),
                _trayStartItem,
                _trayStopItem,
                new NativeMenuItemSeparator(),
                _trayStartupItem,
                new NativeMenuItemSeparator(),
                exitItem
            }
        };

        var trayIcon = new TrayIcon
        {
            Icon = LoadWindowIcon(),
            Menu = menu,
            ToolTipText = "MobileToPcInput 接收器",
            IsVisible = true
        };
        trayIcon.Clicked += (_, _) => RestoreWindow();
        return trayIcon;
    }

    private static WindowIcon LoadWindowIcon()
    {
        // Decode the PNG at runtime and let Avalonia create the native HICON.
        // Some Windows 10 builds render PNG-compressed ICO frames as a generic
        // .ico document icon in the taskbar or notification area.
        return new WindowIcon(AssetLoader.Open(new Uri("avares://MobileToPcInput/Assets/app.png")));
    }

    private void RestoreWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void SetStartupEnabled(bool enabled)
    {
        try
        {
            _startupService.SetEnabled(enabled);
            var actual = _startupService.IsEnabled();
            StartupBox.IsChecked = actual;
            _trayStartupItem.IsChecked = actual;
            _settings.StartupEnabled = actual;
            SaveSettings();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Startup setting failed", ex);
            SetStatus($"● 开机自启设置失败: {ex.Message}", "#C13830", "#FFF1F0");
            var actual = _startupService.IsEnabled();
            StartupBox.IsChecked = actual;
            _trayStartupItem.IsChecked = actual;
            _settings.StartupEnabled = actual;
            SaveSettings();
        }
    }

    private void SetReplaceTrailingFullStopWithSpace(bool enabled)
    {
        ReplaceTrailingFullStopBox.IsChecked = enabled;
        _asrService.ReplaceTrailingFullStopWithSpaceEnabled = enabled;
        _settings.ReplaceTrailingFullStopWithSpace = enabled;
        SaveSettings();
    }

    private void SyncListeningUi(bool isListening)
    {
        StartButton.IsEnabled = !isListening;
        StopButton.IsEnabled = isListening;
        _trayStartItem.IsEnabled = !isListening;
        _trayStopItem.IsEnabled = isListening;
    }

    private void SetStatus(string text, string foreground, string background)
    {
        StatusText.Text = text;
        StatusText.Foreground = Brush(foreground);
        StatusPill.Background = Brush(background);
    }

    private static IBrush Brush(string hex)
    {
        return new SolidColorBrush(Color.Parse(hex));
    }

    private void UpdateConnectQrCode()
    {
        if (!int.TryParse(PortBox.Text, out var port) || port <= 0 || port > 65535)
        {
            QrImage.Source = null;
            return;
        }

        var uri = QrCodeService.BuildConnectUri(NetworkAddressHelper.GetPreferredLocalIp(), port);
        QrImage.Source = QrCodeService.CreateBitmap(uri);
    }

    private async Task FinishAsrSessionAsync()
    {
        if (_isRecognizing)
        {
            AppLogger.Info("ASR finish skipped because recognition is already running.");
            return;
        }

        AppLogger.Info("ASR finish requested.");
        var pcmBytes = _asrBuffer.Stop();
        AppLogger.Info($"ASR session captured bytes={pcmBytes.Length}");
        if (pcmBytes.Length < 1600)
        {
            AppLogger.Info("ASR session ignored because it is too short.");
            Dispatcher.UIThread.Post(() => StatusText.Text = "录音太短，已忽略");
            return;
        }

        _isRecognizing = true;
        string? wavPath = null;
        try
        {
            Dispatcher.UIThread.Post(() => StatusText.Text = "正在识别语音...");
            wavPath = _asrBuffer.WriteWavFile(pcmBytes);
            var text = IsOnlineRecognitionSelected()
                ? await RecognizeOnlineAsync(wavPath)
                : await _asrService.RecognizeAsync(wavPath);
            if (string.IsNullOrWhiteSpace(text))
            {
                AppLogger.Info("ASR returned empty text.");
                Dispatcher.UIThread.Post(() => StatusText.Text = "没有识别到文本");
                return;
            }

            await TextInputService.TypeTextAsync(text);
            AppLogger.Info($"ASR text typed. length={text.Length}");
            Dispatcher.UIThread.Post(() => StatusText.Text = $"已输入: {text}");
        }
        catch (Exception ex)
        {
            AppLogger.Error("ASR recognition failed", ex);
            Dispatcher.UIThread.Post(() => StatusText.Text = $"识别失败: {ex.Message}");
        }
        finally
        {
            _isRecognizing = false;
            Dispatcher.UIThread.Post(() => LevelBar.Value = 0);
            if (wavPath is not null)
            {
                AudioCacheService.TryDelete(wavPath);
            }
        }
    }

    private async Task WarmUpAsrAsync(AsrModelOption? model = null, bool startListeningWhenReady = false)
    {
        try
        {
            model ??= _asrService.CurrentModel;
            AppLogger.Info($"ASR warm-up starting. model={model.Id}, downloaded={model.IsDownloaded}");
            _isAsrReady = false;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                SetStatus("● 正在加载模型...", "#1769E0", "#EEF6FF");
                StartButton.IsEnabled = false;
                ManageModelButton.IsEnabled = false;
                DeviceBox.IsEnabled = false;
            });
            await _asrService.ConfigureModelAsync(model);
            await _asrService.WarmUpAsync();
            _isAsrReady = true;
            AppLogger.Info($"ASR warm-up completed. model={model.Id}");
            var shouldStartListening = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StartButton.IsEnabled = !StopButton.IsEnabled;
                ManageModelButton.IsEnabled = true;
                DeviceBox.IsEnabled = !StopButton.IsEnabled;
                if (!StopButton.IsEnabled)
                {
                    SetStatus("● 未监听，模型已就绪", "#C13830", "#FFF1F0");
                }
                else
                {
                    StatusText.Text = "模型已就绪";
                }
                return startListeningWhenReady && !StopButton.IsEnabled;
            });

            if (shouldStartListening)
            {
                await StartListeningAsync();
            }
        }
        catch (Exception ex)
        {
            _isAsrReady = false;
            AppLogger.Error("ASR warm-up failed", ex);
            Dispatcher.UIThread.Post(() =>
            {
                StartButton.IsEnabled = !StopButton.IsEnabled;
                ManageModelButton.IsEnabled = true;
                DeviceBox.IsEnabled = !StopButton.IsEnabled;
                StatusText.Text = $"模型加载失败: {ex.Message}";
            });
        }
    }

    private async Task WarmUpAsrOnStartupAsync()
    {
        if (IsWeTypeRecognitionSelected())
        {
            _isAsrReady = true;
            RefreshModels();
            await StartListeningAsync();
            return;
        }

        if (IsOnlineRecognitionSelected())
        {
            if (string.IsNullOrWhiteSpace(_settings.XiaomiMimoApiKey))
            {
                _isAsrReady = false;
                StatusText.Text = "请先在模型管理中配置小米 MiMo API Key";
                return;
            }

            _isAsrReady = true;
            SetStatus("● 在线服务已就绪", "#1769E0", "#EEF6FF");
            await StartListeningAsync();
            return;
        }

        var model = GetSelectedLocalModel() ?? _asrService.CurrentModel;
        if (!model.IsDownloaded)
        {
            _isAsrReady = false;
            AppLogger.Info($"ASR startup warm-up skipped because model is not downloaded. model={model.Id}");
            StatusText.Text = "模型未下载，请先打开模型管理";
            return;
        }

        await WarmUpAsrAsync(model, startListeningWhenReady: true);
    }

    private static bool IsStartControl(string? type)
    {
        return type is "asr-start" or "vocotype-start";
    }

    private static bool IsStopControl(string? type)
    {
        return type is "asr-stop" or "vocotype-stop";
    }

    private void ClearAudioCacheButton_Click(object? sender, RoutedEventArgs e)
    {
        ClearAudioCache(showStatus: true);
    }

    private void ClearAudioCache(bool showStatus)
    {
        var count = AudioCacheService.Clear();
        AppLogger.Info($"Audio cache cleared. files={count}, directory={AudioCacheService.CacheDirectory}");
        if (showStatus)
        {
            StatusText.Text = count == 0 ? "语音缓存已清空" : $"已清理 {count} 个语音缓存文件";
        }
    }

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e)
    {
        Hide();
    }

    private async void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        var shouldClose = await ShowCloseConfirmationAsync();
        if (shouldClose)
        {
            ExitApplication();
        }
    }

    private string? GetConfiguredModelId()
    {
        if (string.IsNullOrWhiteSpace(_settings.SelectedModelId))
        {
            return null;
        }

        var model = AsrModelCatalog.Models.FirstOrDefault(item => item.Id == _settings.SelectedModelId);
        return model?.IsDownloaded == true ? model.Id : null;
    }

    private void SavePortSetting()
    {
        if (!int.TryParse(PortBox.Text, out var port) || !IsValidPort(port))
        {
            return;
        }

        _settings.Port = port;
        SaveSettings();
    }

    private void SaveSelectedModelSetting(AsrModelOption model)
    {
        _settings.RecognitionMode = RecognitionModes.Local;
        _settings.SelectedModelId = model.Id;
        SaveSettings();
    }

    private void SaveXiaomiSettings(string apiKey, string language)
    {
        _settings.XiaomiMimoApiKey = apiKey.Trim();
        _settings.XiaomiMimoLanguage = XiaomiMimoAsrService.NormalizeLanguage(language);
        _settings.SelectedOnlineServiceId = OnlineAsrCatalog.XiaomiMimoServiceId;
        SaveSettings();
    }

    private async Task SwitchRecognitionModeFromManagerAsync(bool useOnlineService, string apiKey, string language)
    {
        if (_asrBuffer.IsRecording || _isRecognizing)
        {
            throw new InvalidOperationException("正在录音或识别中，完成后再切换识别服务");
        }

        SaveXiaomiSettings(apiKey, language);
        if (useOnlineService)
        {
            _settings.RecognitionMode = RecognitionModes.Online;
            _settings.SelectedOnlineServiceId = OnlineAsrCatalog.XiaomiMimoServiceId;
            SaveSettings();
            await _asrService.StopWorkerAsync();
            _isAsrReady = !string.IsNullOrWhiteSpace(_settings.XiaomiMimoApiKey);
            SetStatus(
                _isAsrReady ? "● 小米 MiMo ASR 已启用" : "● 请先填写小米 MiMo API Key",
                _isAsrReady ? "#1769E0" : "#C13830",
                _isAsrReady ? "#EEF6FF" : "#FFF1F0");
            RefreshModels();
            RefreshRecognitionModePicker();
            UpdateModelUi();
            return;
        }

        _settings.RecognitionMode = RecognitionModes.Local;
        var model = GetSelectedLocalModel() ?? AsrModelCatalog.DefaultModel;
        if (!model.IsSupported)
        {
            throw new InvalidOperationException("当前本地模型暂不支持");
        }

        if (!model.IsDownloaded)
        {
            throw new InvalidOperationException("当前本地模型未下载，请先下载后再切回本地");
        }

        await WarmUpAsrAsync(model, startListeningWhenReady: true);
        SaveSelectedModelSetting(model);
        RefreshModels();
        RefreshRecognitionModePicker();
        UpdateModelUi();
    }

    private string? GetActiveOnlineServiceId()
    {
        return IsOnlineRecognitionSelected() ? _settings.SelectedOnlineServiceId : null;
    }

    private bool IsOnlineRecognitionSelected()
    {
        return string.Equals(_settings.RecognitionMode, RecognitionModes.Online, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_settings.SelectedOnlineServiceId, OnlineAsrCatalog.XiaomiMimoServiceId, StringComparison.Ordinal);
    }

    private bool IsWeTypeRecognitionSelected()
    {
        return string.Equals(_settings.RecognitionMode, RecognitionModes.WeType, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCableOutputCapture(string? deviceName)
    {
        return !string.IsNullOrWhiteSpace(deviceName)
            && deviceName.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase);
    }

    private void StartHotkeyCapture()
    {
        if (!IsWeTypeRecognitionSelected() || _weTypeBridge.IsActive)
        {
            return;
        }

        _capturedHotkeyTokens.Clear();
        _isCapturingHotkey = true;
        HotkeyCaptureButton.Content = "请同时按键…";
        HotkeyCaptureHint.Text = "正在录制：按下一个或多个按键；Esc 取消";
        UpdateHotkeyButtonsEnabled();
        HotkeyCaptureButton.Focus();
    }

    private void CaptureHotkeyKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_isCapturingHotkey)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            CancelHotkeyCapture();
            e.Handled = true;
            return;
        }

        var eventToken = BridgeHotkeyDefinition.TryGetToken(e.Key, out var token)
            ? token
            : null;
        var pressedTokens = BridgeHotkeyCapture.ResolveKeyDownTokens(
            BridgeHotkeyCapture.GetPressedTokens(),
            eventToken);
        foreach (var pressedToken in pressedTokens)
        {
            if (!_capturedHotkeyTokens.Contains(pressedToken, StringComparer.OrdinalIgnoreCase))
            {
                _capturedHotkeyTokens.Add(pressedToken);
            }
        }

        HotkeyCaptureButton.Content = string.Join(" + ", _capturedHotkeyTokens);
        HotkeyCaptureHint.Text = _capturedHotkeyTokens.Count >= 1
            ? "松开任意按键即可保存"
            : "请按下一个受支持的按键";
        e.Handled = true;
    }

    private void CaptureHotkeyKeyUp(object? sender, KeyEventArgs e)
    {
        if (!_isCapturingHotkey)
        {
            return;
        }

        e.Handled = true;
        if (!BridgeHotkeyDefinition.TryCreate(_capturedHotkeyTokens, out var hotkey))
        {
            CancelHotkeyCapture();
            SetStatus("● 快捷键至少需要一个受支持的按键", "#C13830", "#FFF1F0");
            return;
        }

        _settings.BridgeHotkey = hotkey.SerializedValue;
        _weTypeHotkey.SetHotkey(hotkey.ForSession(_settings.BridgeHotkeyEnabled));
        SaveSettings();
        _isCapturingHotkey = false;
        _capturedHotkeyTokens.Clear();
        HotkeyCaptureButton.Content = hotkey.DisplayName;
        HotkeyCaptureHint.Text = "点击右侧按键，可录入一个或多个按键；Esc 取消";
        SetStatus($"● 已保存按住说话快捷键：{hotkey.DisplayName}", "#1769E0", "#EEF6FF");
        UpdateModelUi();
    }

    private void SetBridgeHotkeyEnabled(bool enabled)
    {
        if (_weTypeBridge.IsActive)
        {
            BridgeHotkeyEnabledBox.IsChecked = _settings.BridgeHotkeyEnabled;
            return;
        }

        CancelHotkeyCapture();
        _settings.BridgeHotkeyEnabled = enabled;
        var configuredHotkey = BridgeHotkeyDefinition.Parse(_settings.BridgeHotkey);
        _weTypeHotkey.SetHotkey(configuredHotkey.ForSession(enabled));
        SaveSettings();
        SetStatus(
            enabled
                ? $"● 已启用按住说话快捷键：{configuredHotkey.DisplayName}"
                : "● 已关闭自动按键，桥接时仅转发音频",
            "#1769E0",
            "#EEF6FF");
        UpdateModelUi();
    }

    private void UpdateHotkeyButtonsEnabled()
    {
        var canEdit = IsWeTypeRecognitionSelected() && !_weTypeBridge.IsActive;
        HotkeyCaptureButton.IsEnabled = canEdit;
        BridgeHotkeyEnabledBox.IsEnabled = canEdit && !_isCapturingHotkey;
    }

    private void CancelHotkeyCapture()
    {
        if (!_isCapturingHotkey)
        {
            return;
        }

        _isCapturingHotkey = false;
        _capturedHotkeyTokens.Clear();
        HotkeyCaptureButton.Content = BridgeHotkeyDefinition.Parse(_settings.BridgeHotkey).DisplayName;
        HotkeyCaptureHint.Text = "点击右侧按键，可录入一个或多个按键；Esc 取消";
        UpdateHotkeyButtonsEnabled();
    }

    private void RefreshRecognitionModePicker()
    {
        _isRefreshingMode = true;
        try
        {
            ModeBox.SelectedItem = RecognitionModeOptions.First(
                item => item.Id == RecognitionModes.Normalize(_settings.RecognitionMode));
        }
        finally
        {
            _isRefreshingMode = false;
        }
    }

    private async Task ApplyRecognitionModeAsync()
    {
        if (_isRefreshingMode || ModeBox.SelectedItem is not RecognitionModeOption option)
        {
            return;
        }

        var nextMode = RecognitionModes.Normalize(option.Id);
        if (nextMode == RecognitionModes.Normalize(_settings.RecognitionMode))
        {
            UpdateModelUi();
            return;
        }

        _weTypeBridge.Abort();
        _audioOutput.Stop();
        _settings.RecognitionMode = nextMode;
        SaveSettings();

        if (nextMode == RecognitionModes.WeType)
        {
            await _asrService.StopWorkerAsync();
            _isAsrReady = true;
            SetStatus("● 桥接输入已启用", "#1769E0", "#EEF6FF");
        }
        else if (nextMode == RecognitionModes.Online)
        {
            await _asrService.StopWorkerAsync();
            _isAsrReady = !string.IsNullOrWhiteSpace(_settings.XiaomiMimoApiKey);
            SetStatus(
                _isAsrReady ? "● 在线服务已启用" : "● 请先配置在线服务",
                _isAsrReady ? "#1769E0" : "#C13830",
                _isAsrReady ? "#EEF6FF" : "#FFF1F0");
        }
        else
        {
            var model = AsrModelCatalog.Models.FirstOrDefault(
                item => item.Id == _settings.SelectedModelId)
                ?? AsrModelCatalog.DefaultModel;
            _isAsrReady = false;
            if (model.IsSupported && model.IsDownloaded)
            {
                await WarmUpAsrAsync(model);
            }
            else
            {
                SetStatus("● 本地模型未就绪", "#C13830", "#FFF1F0");
            }
        }

        RefreshModels();
    }

    private async Task<string> RecognizeOnlineAsync(string wavPath)
    {
        Dispatcher.UIThread.Post(() => StatusText.Text = "正在调用小米 MiMo 识别...");
        var text = await _xiaomiMimoAsrService.RecognizeAsync(
            wavPath,
            _settings.XiaomiMimoApiKey,
            _settings.XiaomiMimoLanguage);
        return _settings.ReplaceTrailingFullStopWithSpace
            ? ParaformerAsrService.ReplaceTrailingFullStopWithSpace(text)
            : text;
    }

    private static string GetLanguageDisplayName(string? language)
    {
        return XiaomiMimoAsrService.NormalizeLanguage(language) switch
        {
            "zh" => "中文",
            "en" => "英文",
            _ => "自动语种"
        };
    }

    private void SaveSettings()
    {
        _settingsService.Save(_settings);
    }

    private static bool IsValidPort(int port)
    {
        return port > 0 && port <= 65535;
    }

    private async Task<bool> ShowCloseConfirmationAsync()
    {
        var dialog = new Window
        {
            Width = 360,
            Height = 188,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowDecorations = WindowDecorations.None,
            Background = Brushes.Transparent,
            TransparencyLevelHint =
            [
                WindowTransparencyLevel.Transparent,
                WindowTransparencyLevel.AcrylicBlur,
                WindowTransparencyLevel.None
            ]
        };

        var titleBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };
        titleBar.Children.Add(new TextBlock
        {
            Text = "确认关闭",
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("#1C2739"),
            VerticalAlignment = VerticalAlignment.Center
        });

        var closeButton = new Button
        {
            Width = 34,
            Height = 30,
            MinHeight = 30,
            Padding = new Thickness(0),
            Content = "×",
            FontSize = 16,
            Background = Brush("#F8FAFC"),
            Foreground = Brush("#1F334D"),
            BorderBrush = Brush("#D6E0EC"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        closeButton.Click += (_, _) => dialog.Close(false);
        Grid.SetColumn(closeButton, 1);
        titleBar.Children.Add(closeButton);

        var cancelButton = new Button
        {
            MinHeight = 36,
            Padding = new Thickness(16, 8),
            Content = "取消",
            Background = Brush("#F8FAFC"),
            Foreground = Brush("#1F334D"),
            BorderBrush = Brush("#D6E0EC"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            FontWeight = FontWeight.SemiBold
        };
        cancelButton.Click += (_, _) => dialog.Close(false);

        var confirmButton = new Button
        {
            MinHeight = 36,
            Padding = new Thickness(16, 8),
            Content = "关闭",
            Background = Brush("#C13830"),
            Foreground = Brushes.White,
            BorderBrush = Brush("#C13830"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            FontWeight = FontWeight.SemiBold
        };
        confirmButton.Click += (_, _) => dialog.Close(true);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        actions.Children.Add(cancelButton);
        actions.Children.Add(confirmButton);

        var content = new StackPanel
        {
            Spacing = 18
        };
        content.Children.Add(titleBar);
        content.Children.Add(new TextBlock
        {
            Text = "关闭会停止监听并退出程序。需要后台运行时，请点击最小化到托盘。",
            FontSize = 13,
            Foreground = Brush("#536174"),
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(actions);

        dialog.Content = new Border
        {
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(20),
            Background = Brushes.White,
            BorderBrush = Brush("#D8E1EC"),
            BorderThickness = new Thickness(1),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                Blur = 28,
                OffsetY = 14,
                Color = Color.Parse("#2D17263B")
            }),
            Child = content
        };

        ShowDialogBackdrop();
        try
        {
            return await dialog.ShowDialog<bool>(this);
        }
        finally
        {
            HideDialogBackdrop();
        }
    }

    private void ShowDialogBackdrop()
    {
        DialogBackdrop.IsVisible = true;
        Surface.Effect = new BlurEffect
        {
            Radius = 8
        };
    }

    private void HideDialogBackdrop()
    {
        DialogBackdrop.IsVisible = false;
        Surface.Effect = null;
    }

    private void DragWindow(object? sender, PointerPressedEventArgs e)
    {
        if (IsInsideButton(e.Source as Visual))
        {
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void DragWindowFromTopArea(object? sender, PointerPressedEventArgs e)
    {
        if (IsInsideButton(e.Source as Visual))
        {
            return;
        }

        var point = e.GetPosition(this);
        if (point.Y > TopDragHeight || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        BeginMoveDrag(e);
    }

    private static bool IsInsideButton(Visual? visual)
    {
        while (visual != null)
        {
            if (visual is Button)
            {
                return true;
            }

            visual = visual.GetVisualParent();
        }

        return false;
    }

    private sealed class ActionProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value)
        {
            report(value);
        }
    }

    private void ExitApplication()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        StopServer();
        _server.Dispose();
        _weTypeBridge.Dispose();
        _weTypeHotkey.Dispose();
        _audioOutput.Dispose();
        _asrService.Dispose();
        _trayIcon.Dispose();
        base.OnClosed(e);
    }
}
