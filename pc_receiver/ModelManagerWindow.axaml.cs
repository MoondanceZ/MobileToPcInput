using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

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
    private bool _lastOperationIsRunning;

    public ModelManagerWindow()
        : this(
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            static () => { },
            static () => null,
            static () => new ModelOperationSnapshot(false, string.Empty, 0, false),
            static _ => { },
            static _ => { })
    {
    }

    public ModelManagerWindow(
        Func<AsrModelOption, Task> loadModelAsync,
        Func<AsrModelOption, Task> deleteModelAsync,
        Action refreshModels,
        Func<string?> getLoadedModelId,
        Func<ModelOperationSnapshot> getOperationSnapshot,
        Action<Action> subscribeOperationChanged,
        Action<Action> unsubscribeOperationChanged)
    {
        _loadModelAsync = loadModelAsync;
        _deleteModelAsync = deleteModelAsync;
        _refreshModels = refreshModels;
        _getLoadedModelId = getLoadedModelId;
        _getOperationSnapshot = getOperationSnapshot;
        _subscribeOperationChanged = subscribeOperationChanged;
        _unsubscribeOperationChanged = unsubscribeOperationChanged;

        WindowDecorations = WindowDecorations.None;
        CanResize = false;
        TransparencyLevelHint =
        [
            WindowTransparencyLevel.Transparent,
            WindowTransparencyLevel.AcrylicBlur,
            WindowTransparencyLevel.None
        ];
        InitializeComponent();
        Surface.AddHandler(PointerPressedEvent, DragWindowFromTopArea, RoutingStrategies.Tunnel);
        TitleBar.AddHandler(PointerPressedEvent, DragWindow, RoutingStrategies.Tunnel);
        _subscribeOperationChanged(OnModelOperationChanged);
        _lastOperationIsRunning = _getOperationSnapshot().IsRunning;
        RenderModels();
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
        StatusText.Text = string.IsNullOrWhiteSpace(snapshot.Message)
            ? "切换模型后会重新加载识别引擎；模型文件保存在 ModelScope 本地缓存中。"
            : snapshot.Message;
        var showProgress = snapshot.IsRunning && snapshot.Progress < 100;
        DownloadProgress.IsVisible = showProgress;
        DownloadProgress.IsIndeterminate = showProgress && snapshot.IsIndeterminate;
        DownloadProgress.Value = showProgress ? snapshot.Progress : 0;
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

        if (e.GetPosition(this).Y <= 90 && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
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

    private static IBrush Brush(string hex)
    {
        return new SolidColorBrush(Color.Parse(hex));
    }
}
