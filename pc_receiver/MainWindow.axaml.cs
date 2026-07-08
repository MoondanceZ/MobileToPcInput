using System;
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
    private readonly AudioOutputService _audioOutput = new();
    private readonly AudioReceiverServer _server = new();
    private readonly AsrSessionBuffer _asrBuffer = new();
    private readonly ParaformerAsrService _asrService = new();
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
    private bool _isApplyingSelectedModel;
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
        InitializeComponent();
        Surface.AddHandler(PointerPressedEvent, DragWindowFromTopArea, RoutingStrategies.Tunnel);
        TitleBar.AddHandler(PointerPressedEvent, DragWindow, RoutingStrategies.Tunnel);
        SetAppImages();
        PortBox.Text = IsValidPort(_settings.Port) ? _settings.Port.ToString() : "8765";
        var startupEnabled = _startupService.IsEnabled();
        StartupBox.IsChecked = startupEnabled;
        _settings.StartupEnabled = startupEnabled;
        SaveSettings();
        StartupBox.Click += (_, _) => SetStartupEnabled(StartupBox.IsChecked == true);
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
                    if (_asrBuffer.IsRecording)
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
                _asrBuffer.AddSamples(bytes);
                var level = AudioLevelMeter.CalculatePercent(bytes);
                Dispatcher.UIThread.Post(() => LevelBar.Value = level);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Audio output failed", ex);
                Dispatcher.UIThread.Post(() => StatusText.Text = $"音频输出失败: {ex.Message}");
            }
        };
        _server.ControlMessageReceived += message =>
        {
            try
            {
                using var document = JsonDocument.Parse(message);
                if (document.RootElement.TryGetProperty("type", out var type)
                    && IsStartControl(type.GetString()))
                {
                    if (!_isAsrReady)
                    {
                        AppLogger.Info($"ASR start rejected because worker is not ready. control={type.GetString()}");
                        Dispatcher.UIThread.Post(() => StatusText.Text = "语音模型加载中，请稍后再说话");
                        return;
                    }

                    AppLogger.Info($"ASR session started by control: {type.GetString()}");
                    _asrBuffer.Start();
                    Dispatcher.UIThread.Post(() => StatusText.Text = "正在录音，松开后识别");
                }
                else if (document.RootElement.TryGetProperty("type", out type)
                    && IsStopControl(type.GetString()))
                {
                    if (!_asrBuffer.IsRecording)
                    {
                        AppLogger.Info($"ASR stop ignored because no recording session is active. control={type.GetString()}");
                        return;
                    }

                    AppLogger.Info($"ASR session stopping by control: {type.GetString()}");
                    _ = FinishAsrSessionAsync();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Control message failed", ex);
            }
        };
        _server.StatusChanged += message =>
        {
            Dispatcher.UIThread.Post(() => StatusText.Text = message);
        };
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
            DeviceBox.IsEnabled = false;
            ManageModelButton.IsEnabled = true;
            SetStatus($"● 正在监听 0.0.0.0:{port}", "#1769E0", "#EEF6FF");
            SyncListeningUi(isListening: true);
        }
        catch (Exception ex)
        {
            SetStatus($"● 启动失败: {ex.Message}", "#C13830", "#FFF1F0");
            SyncListeningUi(isListening: false);
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
            DeviceBox.ItemsSource = AsrModelCatalog.Models.ToArray();
            DeviceBox.SelectedItem = AsrModelCatalog.Models.FirstOrDefault(model => model.Id == selectedId)
                ?? AsrModelCatalog.DefaultModel;
            HintText.Text = "当前使用本机离线语音识别；模型可在“模型管理”中维护。";
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
        SyncListeningUi(isListening: false);
        PortBox.IsEnabled = true;
        DeviceBox.IsEnabled = true;
        ManageModelButton.IsEnabled = true;
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
            handler => ModelOperationChanged -= handler);
        await window.ShowDialog(this);
        RefreshModels();
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
            await WarmUpAsrAsync(model);
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
        });

        if (_isModelOperationRunning)
        {
            SetModelOperation(isRunning: true, message: text, progress, isIndeterminate: false);
        }
    }

    private AsrModelOption? GetSelectedModel()
    {
        return DeviceBox.SelectedItem as AsrModelOption;
    }

    private void UpdateModelUi()
    {
        var model = GetSelectedModel();
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

        var model = GetSelectedModel();
        if (model is null)
        {
            return;
        }

        UpdateModelUi();
        if (model.Id == _asrService.CurrentModel.Id)
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

            await WarmUpAsrAsync(model);
            SaveSelectedModelSetting(model);
        }
        finally
        {
            _isApplyingSelectedModel = false;
        }
    }

    private async Task<bool> EnsureSelectedModelReadyAsync()
    {
        var model = GetSelectedModel();
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
        return new WindowIcon(AssetLoader.Open(new Uri("avares://MobileToPcInput/Assets/app.ico")));
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
            var text = await _asrService.RecognizeAsync(wavPath);
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

    private async Task WarmUpAsrAsync(AsrModelOption? model = null)
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
            Dispatcher.UIThread.Post(() =>
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
            });
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
        var model = GetSelectedModel() ?? _asrService.CurrentModel;
        if (!model.IsDownloaded)
        {
            _isAsrReady = false;
            AppLogger.Info($"ASR startup warm-up skipped because model is not downloaded. model={model.Id}");
            StatusText.Text = "模型未下载，请先打开模型管理";
            return;
        }

        await WarmUpAsrAsync(model);
        if (_isAsrReady && !StopButton.IsEnabled)
        {
            await StartListeningAsync();
        }
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
        _settings.SelectedModelId = model.Id;
        SaveSettings();
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

        return await dialog.ShowDialog<bool>(this);
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
        _audioOutput.Dispose();
        _asrService.Dispose();
        _trayIcon.Dispose();
        base.OnClosed(e);
    }
}
