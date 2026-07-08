using System;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
    private readonly StartupService _startupService = new();
    private readonly TrayIcon _trayIcon;
    private readonly NativeMenuItem _trayStartItem;
    private readonly NativeMenuItem _trayStopItem;
    private readonly NativeMenuItem _trayStartupItem;
    private const double TopDragHeight = 156;
    private bool _allowClose;

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
        InitializeComponent();
        Surface.AddHandler(PointerPressedEvent, DragWindowFromTopArea, RoutingStrategies.Tunnel);
        TitleBar.AddHandler(PointerPressedEvent, DragWindow, RoutingStrategies.Tunnel);
        SetAppImages();
        StartupBox.IsChecked = _startupService.IsEnabled();
        StartupBox.Click += (_, _) => SetStartupEnabled(StartupBox.IsChecked == true);

        _trayStartItem = new NativeMenuItem("开始监听");
        _trayStopItem = new NativeMenuItem("停止监听") { IsEnabled = false };
        _trayStartupItem = new NativeMenuItem("开机自动启动")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = StartupBox.IsChecked == true
        };
        _trayIcon = CreateTrayIcon();

        IpText.Text = NetworkAddressHelper.GetDisplayText();
        RefreshDevices();

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
                    VocoTypeController.ReleaseIfHeld();
                }
            });
        };
        _server.AudioFrameReceived += bytes =>
        {
            try
            {
                _audioOutput.AddSamples(bytes);
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
                    && type.GetString() == "vocotype-start")
                {
                    VocoTypeController.PressF2();
                    Dispatcher.UIThread.Post(() => StatusText.Text = "已按下 VocoType F2");
                }
                else if (document.RootElement.TryGetProperty("type", out type)
                    && type.GetString() == "vocotype-stop")
                {
                    VocoTypeController.ReleaseF2();
                    Dispatcher.UIThread.Post(() => StatusText.Text = "已松开 VocoType F2");
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
        if (!int.TryParse(PortBox.Text, out var port) || port <= 0 || port > 65535)
        {
            StatusText.Text = "端口不正确";
            return;
        }

        if (DeviceBox.SelectedItem is not AudioOutputDevice device)
        {
            StatusText.Text = "请选择输出设备";
            return;
        }

        try
        {
            _audioOutput.Start(device.DeviceNumber);
            await _server.StartAsync(port);
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            PortBox.IsEnabled = false;
            DeviceBox.IsEnabled = false;
            SetStatus($"● 正在监听 0.0.0.0:{port}", "#1769E0", "#EEF6FF");
            SyncListeningUi(isListening: true);
        }
        catch (Exception ex)
        {
            _audioOutput.Stop();
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
        RefreshDevices();
    }

    private void RefreshDevices()
    {
        var devices = _audioOutput.GetDevices();
        DeviceBox.ItemsSource = devices;
        DeviceBox.SelectedItem = devices.FirstOrDefault(d => d.IsLikelyVirtualCable)
                                 ?? devices.FirstOrDefault();
        HintText.Text = devices.Any(d => d.IsLikelyVirtualCable)
            ? "已发现疑似虚拟音频线设备。"
            : "未发现 CABLE / Voicemeeter / Virtual 等常见虚拟音频线设备，请先安装或启用 VB-CABLE。";
    }

    private void StopServer()
    {
        _server.Stop();
        _audioOutput.Stop();
        SyncListeningUi(isListening: false);
        PortBox.IsEnabled = true;
        DeviceBox.IsEnabled = true;
        SetStatus("● 未监听", "#C13830", "#FFF1F0");
        ClientText.Text = "手机未连接";
        LevelBar.Value = 0;
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
        }
        catch (Exception ex)
        {
            AppLogger.Error("Startup setting failed", ex);
            SetStatus($"● 开机自启设置失败: {ex.Message}", "#C13830", "#FFF1F0");
            var actual = _startupService.IsEnabled();
            StartupBox.IsChecked = actual;
            _trayStartupItem.IsChecked = actual;
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

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
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
        _trayIcon.Dispose();
        base.OnClosed(e);
    }
}
