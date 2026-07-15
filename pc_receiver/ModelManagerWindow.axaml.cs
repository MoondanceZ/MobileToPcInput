using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace pc_receiver;

public partial class ModelManagerWindow : Window
{
    private readonly Func<AsrModelOption, Task> _loadModelAsync;
    private readonly Func<AsrModelOption, Task> _deleteModelAsync;
    private readonly Action _refreshModels;
    private readonly Func<string?> _getLoadedModelId;
    private readonly Func<ModelOperationSnapshot> _getOperationSnapshot;
    private readonly Action<Action> _subscribeOperationChanged;
    private readonly Action<Action> _unsubscribeOperationChanged;
    private readonly Func<AppSettings> _getSettings;
    private readonly Action<string, string> _saveXiaomiSettings;
    private readonly Func<bool, string, string, Task> _switchRecognitionModeAsync;
    private readonly Func<string?> _getActiveOnlineServiceId;
    private bool _lastOperationIsRunning;
    private bool _showingOnlineServices;
    private string _xiaomiLanguage = "auto";

    public ModelManagerWindow()
        : this(
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            static () => { },
            static () => null,
            static () => new ModelOperationSnapshot(false, string.Empty, 0, false),
            static _ => { },
            static _ => { },
            static () => new AppSettings(),
            static (_, _) => { },
            static (_, _, _) => Task.CompletedTask,
            static () => null)
    {
    }

    public ModelManagerWindow(
        Func<AsrModelOption, Task> loadModelAsync,
        Func<AsrModelOption, Task> deleteModelAsync,
        Action refreshModels,
        Func<string?> getLoadedModelId,
        Func<ModelOperationSnapshot> getOperationSnapshot,
        Action<Action> subscribeOperationChanged,
        Action<Action> unsubscribeOperationChanged,
        Func<AppSettings> getSettings,
        Action<string, string> saveXiaomiSettings,
        Func<bool, string, string, Task> switchRecognitionModeAsync,
        Func<string?> getActiveOnlineServiceId)
    {
        _loadModelAsync = loadModelAsync;
        _deleteModelAsync = deleteModelAsync;
        _refreshModels = refreshModels;
        _getLoadedModelId = getLoadedModelId;
        _getOperationSnapshot = getOperationSnapshot;
        _subscribeOperationChanged = subscribeOperationChanged;
        _unsubscribeOperationChanged = unsubscribeOperationChanged;
        _getSettings = getSettings;
        _saveXiaomiSettings = saveXiaomiSettings;
        _switchRecognitionModeAsync = switchRecognitionModeAsync;
        _getActiveOnlineServiceId = getActiveOnlineServiceId;

        WindowDecorations = WindowDecorations.None;
        CanResize = false;
        TransparencyLevelHint =
        [
            WindowTransparencyLevel.Transparent,
            WindowTransparencyLevel.AcrylicBlur,
            WindowTransparencyLevel.None
        ];
        InitializeComponent();
        _subscribeOperationChanged(OnModelOperationChanged);
        _lastOperationIsRunning = _getOperationSnapshot().IsRunning;
        LoadOnlineSettings();
        _showingOnlineServices = IsOnlineServiceActive();
        LocalModelsHost.IsVisible = !_showingOnlineServices;
        OnlineServicesHost.IsVisible = _showingOnlineServices;
        RenderModels();
        RenderOnlineServices();
    }

    private void RenderModels()
    {
        ModelsPanel.Children.Clear();
        foreach (var model in AsrModelCatalog.Models)
        {
            ModelsPanel.Children.Add(CreateModelRow(model));
        }

        ApplyOperationSnapshot();
    }

    private void LoadOnlineSettings()
    {
        var settings = _getSettings();
        XiaomiApiKeyBox.Text = settings.XiaomiMimoApiKey;
        _xiaomiLanguage = XiaomiMimoAsrService.NormalizeLanguage(settings.XiaomiMimoLanguage);
        ApplyLanguageButtons();
    }

    private void RenderOnlineServices()
    {
        EnableXiaomiButton.Content = "保存";
        EnableXiaomiButton.IsEnabled = !_getOperationSnapshot().IsRunning;
        EnableXiaomiButton.Background = Brush("#1769E0");
        EnableXiaomiButton.Foreground = Brushes.White;
        EnableXiaomiButton.BorderBrush = Brush("#1769E0");
        ApplyTabState();
    }

    private async Task SetTabAsync(bool online)
    {
        _showingOnlineServices = online;
        LocalModelsHost.IsVisible = !online;
        OnlineServicesHost.IsVisible = online;
        ApplyTabState();
        ApplyOperationSnapshot();
        SaveXiaomiSettings();

        try
        {
            await _switchRecognitionModeAsync(online, XiaomiApiKeyBox.Text ?? string.Empty, _xiaomiLanguage);
            RenderOnlineServices();
            ApplyOperationSnapshot();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void ApplyTabState()
    {
        LocalTabSelection.IsVisible = !_showingOnlineServices;
        OnlineTabSelection.IsVisible = _showingOnlineServices;
        OnlineTabSelection.Background = Brush("#1769E0");
        LocalTabLabel.Foreground = _showingOnlineServices ? Brush("#536174") : Brushes.White;
        OnlineTabLabel.Foreground = _showingOnlineServices ? Brushes.White : Brush("#536174");
    }

    private bool IsOnlineServiceActive()
    {
        return _getActiveOnlineServiceId() == OnlineAsrCatalog.XiaomiMimoServiceId;
    }

    private void SaveXiaomiSettings()
    {
        _saveXiaomiSettings(XiaomiApiKeyBox.Text ?? string.Empty, _xiaomiLanguage);
    }

    private Control CreateModelRow(AsrModelOption model)
    {
        var isLoaded = model.Id == _getLoadedModelId();
        var isBusy = _getOperationSnapshot().IsRunning;
        var isSharedReady = model.IsPunctuationDownloaded && model.IsVadDownloaded;
        var isReady = model.IsDownloaded && isSharedReady;
        var accent = model.IsSupported && (model.IsDownloaded || isLoaded) ? "#1769E0" : "#A0AEC0";
        var background = isLoaded ? "#EAF3FF" : "White";
        var border = isLoaded ? "#6FA8FF" : "#DDE6F1";

        var title = new TextBlock
        {
            Text = model.DisplayName,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("#1C2739"),
            TextWrapping = TextWrapping.Wrap
        };
        var desc = new TextBlock
        {
            Text = model.Description,
            FontSize = 12,
            Foreground = Brush("#718096"),
            TextWrapping = TextWrapping.Wrap
        };
        var action = new Button
        {
            MinHeight = 36,
            Padding = new Thickness(14, 8),
            Content = !model.IsSupported ? "待支持" : isLoaded ? "已加载" : !model.IsDownloaded ? "下载" : !isSharedReady ? "补齐" : "加载",
            IsEnabled = model.IsSupported && !isLoaded && !isBusy,
            Background = isLoaded ? Brush("#E6EEF8") : isReady ? Brush("#1769E0") : Brush("#0F8A6A"),
            Foreground = isLoaded ? Brush("#536174") : Brushes.White,
            BorderBrush = isLoaded ? Brush("#D6E0EC") : isReady ? Brush("#1769E0") : Brush("#0F8A6A"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            FontWeight = FontWeight.SemiBold
        };
        action.Click += async (_, _) => await RunModelActionAsync(model, isDelete: false);

        var delete = new Button
        {
            MinHeight = 36,
            Padding = new Thickness(14, 8),
            Content = "删除",
            IsEnabled = model.IsSupported && model.IsDownloaded && !isLoaded && !isBusy,
            Background = Brush("#FFF1F0"),
            Foreground = Brush("#C13830"),
            BorderBrush = Brush("#FFD5D2"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            FontWeight = FontWeight.SemiBold
        };
        delete.Click += async (_, _) => await RunModelActionAsync(model, isDelete: true);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        actions.Children.Add(action);
        actions.Children.Add(delete);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("32,*,18,Auto"),
            MinHeight = 78,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        var selectionMark = new Grid
        {
            Width = 18,
            Height = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        selectionMark.Children.Add(new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            BorderBrush = Brush(accent),
            BorderThickness = new Thickness(3),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (isLoaded)
        {
            selectionMark.Children.Add(new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = Brush("#1769E0"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        grid.Children.Add(selectionMark);

        var text = new StackPanel
        {
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center
        };
        text.Children.Add(title);
        text.Children.Add(desc);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        Grid.SetColumn(actions, 3);
        grid.Children.Add(actions);

        return new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 14),
            Background = Brush(background),
            BorderBrush = Brush(border),
            BorderThickness = new Thickness(isLoaded ? 1.5 : 1),
            Child = grid
        };
    }

    private async Task RunModelActionAsync(AsrModelOption model, bool isDelete)
    {
        if (_getOperationSnapshot().IsRunning)
        {
            return;
        }

        try
        {
            RenderModels();
            if (isDelete)
            {
                await _deleteModelAsync(model);
            }
            else
            {
                await _loadModelAsync(model);
            }

            _refreshModels();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            RenderModels();
        }
    }

    private void OnModelOperationChanged()
    {
        var snapshot = _getOperationSnapshot();
        if (snapshot.IsRunning != _lastOperationIsRunning)
        {
            _lastOperationIsRunning = snapshot.IsRunning;
            RenderModels();
            return;
        }

        ApplyOperationSnapshot(snapshot);
    }

    private void ApplyOperationSnapshot()
    {
        ApplyOperationSnapshot(_getOperationSnapshot());
    }

    private void ApplyOperationSnapshot(ModelOperationSnapshot snapshot)
    {
        StatusText.Text = _showingOnlineServices && !snapshot.IsRunning
            ? "当前使用：小米 MiMo ASR · " + GetLanguageDisplayName(_xiaomiLanguage)
            : string.IsNullOrWhiteSpace(snapshot.Message)
            ? _showingOnlineServices
                ? "当前使用：小米 MiMo ASR · " + GetLanguageDisplayName(_xiaomiLanguage)
                : "切换模型后会重新加载识别引擎；模型文件保存在 ModelScope 本地缓存中。"
            : snapshot.Message;
        var showProgress = snapshot.IsRunning && snapshot.Progress < 100;
        DownloadProgress.IsVisible = showProgress;
        DownloadProgress.IsIndeterminate = showProgress && snapshot.IsIndeterminate;
        DownloadProgress.Value = showProgress ? snapshot.Progress : 0;
        RenderOnlineServices();
    }

    private async void LocalTabHitArea_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        await SetTabAsync(online: false);
    }

    private async void OnlineTabHitArea_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        await SetTabAsync(online: true);
    }

    private void XiaomiApiKeyBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        SaveXiaomiSettings();
    }

    private void LanguageButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string language)
        {
            _xiaomiLanguage = XiaomiMimoAsrService.NormalizeLanguage(language);
            ApplyLanguageButtons();
            SaveXiaomiSettings();
        }
    }

    private async void EnableXiaomiButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_getOperationSnapshot().IsRunning)
        {
            return;
        }

        SaveXiaomiSettings();
        try
        {
            await _switchRecognitionModeAsync(true, XiaomiApiKeyBox.Text ?? string.Empty, _xiaomiLanguage);
            RenderOnlineServices();
            ApplyOperationSnapshot();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void ApplyLanguageButtons()
    {
        ApplyLanguageButton(LanguageAutoSelection, LanguageAutoLabel, _xiaomiLanguage == "auto");
        ApplyLanguageButton(LanguageZhSelection, LanguageZhLabel, _xiaomiLanguage == "zh");
        ApplyLanguageButton(LanguageEnSelection, LanguageEnLabel, _xiaomiLanguage == "en");
    }

    private static void ApplyLanguageButton(Border selection, TextBlock label, bool selected)
    {
        selection.IsVisible = selected;
        label.Foreground = selected ? Brushes.White : Brush("#536174");
        label.FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal;
    }

    private static string GetLanguageDisplayName(string language)
    {
        return XiaomiMimoAsrService.NormalizeLanguage(language) switch
        {
            "zh" => "中文",
            "en" => "英文",
            _ => "自动语种"
        };
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _unsubscribeOperationChanged(OnModelOperationChanged);
        base.OnClosed(e);
    }

    private static IBrush Brush(string hex)
    {
        return new SolidColorBrush(Color.Parse(hex));
    }
}
