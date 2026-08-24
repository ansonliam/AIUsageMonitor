using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AIUsageMonitor.Controls;
using AIUsageMonitor.Converters;
using AIUsageMonitor.Models;
using AIUsageMonitor.Services;
using AIUsageMonitor.ViewModels;
using Forms = System.Windows.Forms;

namespace AIUsageMonitor.Views;

public partial class MainWindow : Window
{
    private const int WmNcHitTest = 0x0084;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
    private const double DashboardMinWidth = 420;
    private const double DashboardMinHeight = 320;
    private static readonly string PlacementPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AIUsageMonitor",
        "window-placement.json");
    private readonly IApplicationController _applicationController;
    private readonly AutoRefreshOptions _autoRefreshOptions;
    private readonly MainViewModel _viewModel;
    // A Grid whose rows are all "*" and that sits inside a ScrollViewer (which measures its
    // content with infinite available height so it can decide whether to show a scrollbar) does
    // not divide its rows evenly - WPF falls back to auto-sizing each star row by its content's
    // desired size in that case, which is unpredictable once cards of different heights are
    // spanning different row combinations. Giving DashboardGrid an explicit total height (this
    // constant times the row count) instead makes every row exactly this many pixels tall,
    // always - which DashboardCard's drag/resize snapping math depends on to convert pixels back
    // to whole cell deltas.
    private const double DefaultDashboardWidgetHeight = 50;
    private const double DefaultMetricLabelWidth = 27;
    private const double DefaultProgressBarHeight = 2;

    private readonly Dictionary<DashboardCardViewModel, DashboardCard> _dashboardElements = [];
    private HwndSource? _windowSource;
    private bool _isDraggingWindow;
    private System.Windows.Point _dragStartPosition;

    // Raised whenever widget state that Settings also displays has changed. Settings is shown
    // non-modally, so the widget's own context menu (opacity, layout, lock, always-on-top, hide)
    // can be used while the Settings window is open - without this, its controls would keep
    // showing the values they were constructed with.
    public event Action? WidgetStateChanged;

    public bool IsWindowLocked { get; private set; }
    public bool IsDashboardLayoutEnabled { get; private set; } = true;
    public double DashboardWidgetHeight { get; private set; } = DefaultDashboardWidgetHeight;
    public double MetricLabelWidth { get; private set; } = DefaultMetricLabelWidth;
    public double ProgressBarHeight { get; private set; } = DefaultProgressBarHeight;
    public bool IsHorizontalLayout { get; private set; }
    public bool ShowDashboardWidget { get; private set; } = true;
    public bool AlwaysOnTop { get; private set; } = true;
    public bool ShowCodex { get; private set; } = true;
    public bool ShowClaude { get; private set; } = true;
    public bool ShowAntigravity { get; private set; } = true;
    public bool ShowCursor { get; private set; } = true;
    public string FontSizePreset { get; private set; } = "Large";
    public string WidgetFont { get; private set; } = "Oxanium";
    public string WidgetAppearance { get; private set; } = "Retro";
    public string WidgetTextWeight { get; private set; } = "Regular";
    public string GreenColorHex { get; private set; } = "#2ECC71";
    public string LimeColorHex { get; private set; } = "#9ACD32";
    public string YellowColorHex { get; private set; } = "#FFD21E";
    public string OrangeColorHex { get; private set; } = "#FF9800";
    public string RedColorHex { get; private set; } = "#FF4D4F";
    public double Stage1MaxPercent { get; private set; } = 29;
    public double Stage2MaxPercent { get; private set; } = 49;
    public double Stage3MaxPercent { get; private set; } = 69;
    public double Stage4MaxPercent { get; private set; } = 79;
    public double Stage5MaxPercent { get; private set; } = 84;
    public bool ShowUsageRemaining { get; private set; }
    public bool AutoRefreshEnabled { get; private set; } = true;
    public double CodexRefreshIntervalMinutes { get; private set; } = AutoRefreshOptions.CodexDefaultIntervalMinutes;
    public double ClaudeRefreshIntervalMinutes { get; private set; } = AutoRefreshOptions.ClaudeDefaultIntervalMinutes;
    public double AntigravityRefreshIntervalMinutes { get; private set; } = AutoRefreshOptions.AntigravityDefaultIntervalMinutes;
    public double CursorRefreshIntervalMinutes { get; private set; } = AutoRefreshOptions.CursorDefaultIntervalMinutes;
    public double IdleAfterMinutes { get; private set; } = AutoRefreshOptions.DefaultIdleAfterMinutes;
    public double IdleRefreshIntervalMinutes { get; private set; } = AutoRefreshOptions.DefaultIdleRefreshIntervalMinutes;
    public double CodexThrottleIntervalMinutes { get; private set; } = AutoRefreshOptions.CodexDefaultThrottleMinutes;
    public double ClaudeThrottleIntervalMinutes { get; private set; } = AutoRefreshOptions.ClaudeDefaultThrottleMinutes;
    public double AntigravityThrottleIntervalMinutes { get; private set; } = AutoRefreshOptions.AntigravityDefaultThrottleMinutes;
    public double CursorThrottleIntervalMinutes { get; private set; } = AutoRefreshOptions.CursorDefaultThrottleMinutes;

    public MainWindow(
        MainViewModel viewModel,
        IApplicationController applicationController,
        AutoRefreshOptions autoRefreshOptions)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
        _applicationController = applicationController;
        _autoRefreshOptions = autoRefreshOptions;
        _viewModel.LayoutChanged += () => Dispatcher.BeginInvoke(ApplyProviderLayout);
        _viewModel.DashboardLayout.Cards.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(RebuildDashboardGrid);
        _viewModel.DashboardLayout.PropertyChanged += DashboardLayout_PropertyChanged;
        DashboardGrid.SizeChanged += (_, _) => RenderDashboardGridLines();
        Loaded += MainWindow_Loaded;
        SourceInitialized += MainWindow_SourceInitialized;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SavePlacement();
        if (System.Windows.Application.Current is App { IsExiting: false })
        {
            e.Cancel = true;
            _ = _applicationController.ExitAsync();
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
        base.OnClosed(e);
    }

    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsWindowLocked ||
            e.ChangedButton != MouseButton.Left ||
            IsInsideButton(e.OriginalSource as DependencyObject) ||
            IsInsideThumb(e.OriginalSource as DependencyObject))
        {
            return;
        }

        _isDraggingWindow = true;
        _dragStartPosition = e.GetPosition(this);
        Mouse.Capture(this);
        e.Handled = true;
    }

    private void Window_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingWindow)
        {
            return;
        }

        var currentPosition = e.GetPosition(null);
        Left += currentPosition.X - (_dragStartPosition.X + Left);
        Top += currentPosition.Y - (_dragStartPosition.Y + Top);
    }

    private void Window_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingWindow)
        {
            return;
        }

        _isDraggingWindow = false;
        Mouse.Capture(null);
        SavePlacement();
        e.Handled = true;
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    // This handler is attached at the Window's preview stage, before a Thumb can begin its own
    // WPF drag. Treat resize/move thumbs as window-drag exclusions; otherwise DragMove captures
    // the pointer for the whole widget and the dashboard card can never receive DragStarted.
    private static bool IsInsideThumb(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.Primitives.Thumb)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void WindowResizeThumb_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (IsWindowLocked)
        {
            return;
        }

        Width = Math.Max(MinWidth, Width + e.HorizontalChange);
        Height = Math.Max(MinHeight, Height + e.VerticalChange);
    }

    private void WindowResizeThumb_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) =>
        SavePlacement();

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var placement = ReadPlacement();
        if (placement is not null && IsOnVirtualDesktop(placement))
        {
            Left = placement.Left;
            Top = placement.Top;
            if (double.IsFinite(placement.Width) && placement.Width >= MinWidth)
            {
                Width = placement.Width;
            }

            if (double.IsFinite(placement.Height) && placement.Height >= MinHeight)
            {
                Height = placement.Height;
            }

            IsWindowLocked = placement.IsLocked;
            IsDashboardLayoutEnabled = placement.IsDashboardLayoutEnabled;
            DashboardWidgetHeight = double.IsFinite(placement.DashboardWidgetHeight) &&
                                    placement.DashboardWidgetHeight >= 1
                ? placement.DashboardWidgetHeight
                : DefaultDashboardWidgetHeight;
            MetricLabelWidth = double.IsFinite(placement.MetricLabelWidth) && placement.MetricLabelWidth >= 1
                ? placement.MetricLabelWidth
                : DefaultMetricLabelWidth;
            ProgressBarHeight = double.IsFinite(placement.ProgressBarHeight) && placement.ProgressBarHeight >= 1
                ? placement.ProgressBarHeight
                : DefaultProgressBarHeight;
            IsHorizontalLayout = placement.IsHorizontalLayout;
            ShowDashboardWidget = placement.ShowDashboardWidget;
            AlwaysOnTop = placement.AlwaysOnTop;
            ShowCodex = placement.ShowCodex;
            ShowClaude = placement.ShowClaude;
            ShowAntigravity = placement.ShowAntigravity;
            ShowCursor = placement.ShowCursor;
            FontSizePreset = NormalizeFontSizePreset(placement.FontSizePreset);
            WidgetFont = NormalizeWidgetFont(
                placement.WidgetFont ?? ExtractWidgetFont(placement.WidgetStyle));
            WidgetAppearance = NormalizeWidgetAppearance(
                placement.WidgetAppearance ?? ExtractWidgetAppearance(placement.WidgetStyle));
            WidgetTextWeight = NormalizeWidgetTextWeight(placement.WidgetTextWeight);
            GreenColorHex = placement.GreenColorHex;
            LimeColorHex = placement.LimeColorHex;
            YellowColorHex = placement.YellowColorHex;
            OrangeColorHex = placement.OrangeColorHex;
            RedColorHex = placement.RedColorHex;
            Stage1MaxPercent = placement.Stage1MaxPercent;
            Stage2MaxPercent = placement.Stage2MaxPercent;
            Stage3MaxPercent = placement.Stage3MaxPercent;
            Stage4MaxPercent = placement.Stage4MaxPercent;
            Stage5MaxPercent = placement.Stage5MaxPercent;
            ShowUsageRemaining = placement.ShowUsageRemaining;
            AutoRefreshEnabled = placement.AutoRefreshEnabled;
            CodexRefreshIntervalMinutes = AutoRefreshOptions.NormalizeInterval(
                placement.CodexRefreshIntervalMinutes);
            ClaudeRefreshIntervalMinutes = AutoRefreshOptions.NormalizeInterval(
                placement.ClaudeRefreshIntervalMinutes);
            AntigravityRefreshIntervalMinutes = AutoRefreshOptions.NormalizeInterval(
                placement.AntigravityRefreshIntervalMinutes);
            CursorRefreshIntervalMinutes = AutoRefreshOptions.NormalizeInterval(
                placement.CursorRefreshIntervalMinutes);
            IdleAfterMinutes = AutoRefreshOptions.NormalizeInterval(placement.IdleAfterMinutes);
            IdleRefreshIntervalMinutes = AutoRefreshOptions.NormalizeInterval(placement.IdleRefreshIntervalMinutes);
            CodexThrottleIntervalMinutes = AutoRefreshOptions.NormalizeThrottle(
                placement.CodexThrottleIntervalMinutes);
            ClaudeThrottleIntervalMinutes = AutoRefreshOptions.NormalizeThrottle(
                placement.ClaudeThrottleIntervalMinutes);
            AntigravityThrottleIntervalMinutes = AutoRefreshOptions.NormalizeThrottle(
                placement.AntigravityThrottleIntervalMinutes);
            CursorThrottleIntervalMinutes = AutoRefreshOptions.NormalizeThrottle(
                placement.CursorThrottleIntervalMinutes);
            Opacity = Math.Clamp(placement.Opacity, 0.6, 1.0);
        }
        else
        {
            Dispatcher.BeginInvoke(() =>
            {
                SnapToBottomRight();
                SavePlacement();
            }, DispatcherPriority.ContextIdle);
        }

        ApplyWindowLockState();
        ApplyAutoRefreshOptions();
        ApplyThrottleOptions();
        ApplySavedUsageColors();
        _viewModel.SetUsageDisplayMode(ShowUsageRemaining);
        ApplyWidgetPresentation();
        ApplyFontSizePreset();
        ApplyMetricLabelWidth();
        ApplyProgressBarHeight();
        ApplyProviderLayout();
        ApplyProviderVisibility();
        ApplyDashboardLayoutMode();
        ApplyDashboardEditModeVisuals();
        Topmost = AlwaysOnTop;
    }

    public void SetWindowLocked(bool isLocked)
    {
        if (IsWindowLocked == isLocked)
        {
            return;
        }

        IsWindowLocked = isLocked;
        ApplyWindowLockState();
        SavePlacement();
    }

    public void SetHorizontalLayout(bool isHorizontal)
    {
        if (IsHorizontalLayout == isHorizontal)
        {
            return;
        }

        IsHorizontalLayout = isHorizontal;
        ApplyProviderLayout();
        SavePlacement();
    }

    public void SetDashboardWidgetVisible(bool isVisible)
    {
        if (ShowDashboardWidget == isVisible)
        {
            return;
        }

        ShowDashboardWidget = isVisible;
        if (isVisible)
        {
            Show();
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Activate();
        }
        else
        {
            Hide();
        }

        SavePlacement();
    }

    public void SetDashboardLayoutEnabled(bool enabled)
    {
        if (IsDashboardLayoutEnabled == enabled)
        {
            return;
        }

        IsDashboardLayoutEnabled = enabled;
        if (!enabled)
        {
            _viewModel.DashboardLayout.IsEditMode = false;
        }

        ApplyDashboardLayoutMode();
        SavePlacement();
    }

    public void SetDashboardWidgetHeight(double height)
    {
        if (!double.IsFinite(height))
        {
            return;
        }

        DashboardWidgetHeight = Math.Max(1, Math.Round(height));
        if (IsDashboardLayoutEnabled)
        {
            RebuildDashboardGrid();
            Dispatcher.BeginInvoke(() =>
            {
                RebuildDashboardGrid();
                FitDashboardPanelToContent();
            }, DispatcherPriority.ContextIdle);
        }

        SavePlacement();
    }

    public void SetMetricLabelWidth(double width)
    {
        if (!double.IsFinite(width))
        {
            return;
        }

        MetricLabelWidth = Math.Max(1, Math.Round(width));
        ApplyMetricLabelWidth();
        SavePlacement();
    }

    public void SetProgressBarHeight(double height)
    {
        if (!double.IsFinite(height))
        {
            return;
        }

        ProgressBarHeight = Math.Max(1, Math.Round(height));
        ApplyProgressBarHeight();
        SavePlacement();
    }

    // Swaps between the normal compact widget content and the free-form dashboard grid. The two
    // are mutually exclusive views of the same underlying Providers/CodexApiCostPanels data -
    // enabling the dashboard does not change what is shown, only how it is arranged and sized.
    private void ApplyDashboardLayoutMode()
    {
        CompactContentScrollViewer.Visibility = IsDashboardLayoutEnabled ? Visibility.Collapsed : Visibility.Visible;
        DashboardScrollViewer.Visibility = IsDashboardLayoutEnabled ? Visibility.Visible : Visibility.Collapsed;

        if (!IsDashboardLayoutEnabled)
        {
            // Compact mode owns MinWidth/MinHeight/Height itself based on live content -
            // ApplyProviderLayout (which calls ApplyCompactHeight) is the single source of truth
            // for that whenever dashboard mode is off. See ApplyCompactHeight's own early-return
            // guard for the other half of this split.
            ApplyProviderLayout();
            return;
        }

        MinWidth = DashboardMinWidth;
        MinHeight = DashboardMinHeight;
        if (Width < DashboardMinWidth)
        {
            Width = 640;
        }

        if (Height < DashboardMinHeight)
        {
            Height = 480;
        }

        RebuildDashboardGrid();
        ApplyDashboardGridOverlaySize();
    }

    private void DashboardLayout_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DashboardLayoutViewModel.Rows):
                Dispatcher.BeginInvoke(() =>
                {
                    RebuildDashboardGrid();
                    ApplyDashboardGridOverlaySize();
                });
                break;
            case nameof(DashboardLayoutViewModel.IsEditMode):
                Dispatcher.BeginInvoke(() =>
                {
                    ApplyDashboardEditModeVisuals();
                    if (!_viewModel.DashboardLayout.IsEditMode)
                    {
                        FitDashboardPanelToContent();
                    }
                }, DispatcherPriority.ContextIdle);
                break;
        }
    }

    // Edit mode deliberately keeps spare parking rows so a card can be left there. Once editing
    // is finished, retain those positions but grow the panel by just its current overflow. This
    // prevents the ScrollViewer from hiding the bottom cards or leaving a vertical scrollbar.
    private void FitDashboardPanelToContent()
    {
        if (!IsDashboardLayoutEnabled)
        {
            return;
        }

        DashboardScrollViewer.UpdateLayout();
        var overflow = DashboardScrollViewer.ScrollableHeight;
        if (overflow <= 0.5)
        {
            return;
        }

        Height += Math.Ceiling(overflow) + 2;
        DashboardScrollViewer.UpdateLayout();
    }

    private void ApplyDashboardEditModeVisuals()
    {
        var isEditing = _viewModel.DashboardLayout.IsEditMode;
        DashboardEditBackground.Visibility = isEditing ? Visibility.Visible : Visibility.Collapsed;
        DashboardGridOverlay.Visibility = isEditing ? Visibility.Visible : Visibility.Collapsed;
        WindowResizeThumb.Visibility = IsDashboardLayoutEnabled && isEditing
            ? Visibility.Visible
            : Visibility.Collapsed;
        RenderDashboardGridLines();
    }

    // Rebuild the exact six-column grid guides when the board's dimensions change. They are real
    // line elements rather than a brush so the edit grid remains visible over the wallpaper and
    // each cell boundary is crisp.
    private void ApplyDashboardGridOverlaySize()
    {
        RenderDashboardGridLines();
    }

    private void RenderDashboardGridLines()
    {
        if (!_viewModel.DashboardLayout.IsEditMode ||
            DashboardGrid.ActualWidth <= 0 ||
            DashboardGrid.ActualHeight <= 0)
        {
            DashboardGridOverlay.Children.Clear();
            return;
        }

        var layout = _viewModel.DashboardLayout;
        var guideBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
        guideBrush.Freeze();
        DashboardGridOverlay.Children.Clear();
        DashboardGridOverlay.Width = DashboardGrid.ActualWidth;
        DashboardGridOverlay.Height = DashboardGrid.ActualHeight;

        for (var column = 0; column <= layout.Columns; column++)
        {
            var x = Math.Round(DashboardGrid.ActualWidth * column / layout.Columns) + 0.5;
            DashboardGridOverlay.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = x,
                X2 = x,
                Y1 = 0,
                Y2 = DashboardGrid.ActualHeight,
                Stroke = guideBrush,
                StrokeThickness = 1,
                SnapsToDevicePixels = true
            });
        }

        for (var row = 0; row <= layout.Rows; row++)
        {
            var y = Math.Round(DashboardGrid.ActualHeight * row / layout.Rows) + 0.5;
            DashboardGridOverlay.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = 0,
                X2 = DashboardGrid.ActualWidth,
                Y1 = y,
                Y2 = y,
                Stroke = guideBrush,
                StrokeThickness = 1,
                SnapsToDevicePixels = true
            });
        }
    }

    // Rebuilds DashboardGrid's structure (row/column definitions) and adds/removes card elements
    // to match DashboardLayoutViewModel.Cards. Deliberately does *not* touch elements for cards
    // that already exist - see DashboardCard_PropertyChanged, which repositions an existing
    // element in place when its card moves or resizes, so an in-progress drag is never
    // interrupted by a structural rebuild triggered by some unrelated card appearing/disappearing.
    private void RebuildDashboardGrid()
    {
        var layout = _viewModel.DashboardLayout;

        if (DashboardGrid.RowDefinitions.Count != layout.Rows)
        {
            DashboardGrid.RowDefinitions.Clear();
            for (var i = 0; i < layout.Rows; i++)
            {
                DashboardGrid.RowDefinitions.Add(new RowDefinition());
            }
        }

        if (DashboardGrid.ColumnDefinitions.Count != layout.Columns)
        {
            DashboardGrid.ColumnDefinitions.Clear();
            for (var i = 0; i < layout.Columns; i++)
            {
                DashboardGrid.ColumnDefinitions.Add(new ColumnDefinition());
            }
        }

        // Explicit height (see DashboardWidgetHeight) rather than letting the Grid size
        // itself - width doesn't need the same treatment since the ScrollViewer never gives it
        // infinite *width* (HorizontalScrollBarVisibility is Disabled), so columns already divide
        // evenly on their own.
        DashboardGrid.Height = layout.Rows * DashboardWidgetHeight;

        var desired = new HashSet<DashboardCardViewModel>(layout.Cards);

        foreach (var stale in _dashboardElements.Keys.Where(card => !desired.Contains(card)).ToList())
        {
            DashboardGrid.Children.Remove(_dashboardElements[stale]);
            stale.PropertyChanged -= DashboardCard_PropertyChanged;
            _dashboardElements.Remove(stale);
        }

        foreach (var card in layout.Cards)
        {
            if (_dashboardElements.ContainsKey(card))
            {
                continue;
            }

            var element = new DashboardCard { DataContext = card, Margin = new Thickness(0) };
            card.PropertyChanged += DashboardCard_PropertyChanged;
            Grid.SetRow(element, card.Row);
            Grid.SetColumn(element, card.Column);
            Grid.SetRowSpan(element, card.RowSpan);
            Grid.SetColumnSpan(element, card.ColumnSpan);
            DashboardGrid.Children.Add(element);
            _dashboardElements[card] = element;
        }
    }

    private void DashboardCard_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not DashboardCardViewModel card || !_dashboardElements.TryGetValue(card, out var element))
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(DashboardCardViewModel.Row):
                Grid.SetRow(element, card.Row);
                break;
            case nameof(DashboardCardViewModel.Column):
                Grid.SetColumn(element, card.Column);
                break;
            case nameof(DashboardCardViewModel.RowSpan):
                Grid.SetRowSpan(element, card.RowSpan);
                break;
            case nameof(DashboardCardViewModel.ColumnSpan):
                Grid.SetColumnSpan(element, card.ColumnSpan);
                break;
        }
    }

    private void HorizontalLayoutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SetDashboardLayoutEnabled(false);
        SetHorizontalLayout(true);
    }

    private void VerticalLayoutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SetDashboardLayoutEnabled(false);
        SetHorizontalLayout(false);
    }

    private void DashboardLayoutMenuItem_Click(object sender, RoutedEventArgs e) =>
        SetDashboardLayoutEnabled(true);

    private void EditDashboardLayoutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (!IsDashboardLayoutEnabled)
        {
            SetDashboardLayoutEnabled(true);
        }

        _viewModel.DashboardLayout.IsEditMode = !_viewModel.DashboardLayout.IsEditMode;
    }

    private void ResetDashboardLayoutMenuItem_Click(object sender, RoutedEventArgs e) =>
        _viewModel.DashboardLayout.ResetLayoutCommand.Execute(null);

    public void SetAlwaysOnTop(bool alwaysOnTop)
    {
        if (AlwaysOnTop == alwaysOnTop)
        {
            return;
        }

        AlwaysOnTop = alwaysOnTop;
        Topmost = AlwaysOnTop;
        SavePlacement();
    }

    public void SetProviderVisibility(ProviderKind provider, bool isVisible)
    {
        if (provider == ProviderKind.Codex)
        {
            ShowCodex = isVisible;
        }
        else if (provider == ProviderKind.Claude)
        {
            ShowClaude = isVisible;
        }
        else if (provider == ProviderKind.Antigravity)
        {
            ShowAntigravity = isVisible;
        }
        else
        {
            ShowCursor = isVisible;
        }

        ApplyProviderVisibility();
        SavePlacement();
    }

    public void SetFontSizePreset(string preset)
    {
        var normalized = NormalizeFontSizePreset(preset);
        if (FontSizePreset == normalized)
        {
            return;
        }

        FontSizePreset = normalized;
        ApplyFontSizePreset();
        ApplyProviderLayout();
        SavePlacement();
    }

    public void SetWidgetFont(string font)
    {
        var normalized = NormalizeWidgetFont(font);
        if (WidgetFont == normalized)
        {
            return;
        }

        WidgetFont = normalized;
        ApplyWidgetPresentation();
        SavePlacement();
    }

    public void SetWidgetAppearance(string appearance)
    {
        var normalized = NormalizeWidgetAppearance(appearance);
        if (WidgetAppearance == normalized)
        {
            return;
        }

        WidgetAppearance = normalized;
        ApplyWidgetPresentation();
        SavePlacement();
    }

    public void SetWidgetTextWeight(string weight)
    {
        var normalized = NormalizeWidgetTextWeight(weight);
        if (WidgetTextWeight == normalized)
        {
            return;
        }

        WidgetTextWeight = normalized;
        ApplyWidgetPresentation();
        SavePlacement();
    }

    private void ApplyWidgetPresentation()
    {
        var usesOriginalFont = WidgetFont == "Segoe UI Variable Text";
        var usesRetroRendering = WidgetAppearance == "Retro";
        var embeddedFontFamilyName = WidgetFont switch
        {
            "VT323" => "VT323",
            "Silkscreen" => "Silkscreen",
            "Tiny5" => "Tiny5",
            "Space Mono" => "Space Mono",
            "Chakra Petch" => "Chakra Petch",
            "IBM Plex Mono" => "IBM Plex Mono",
            "DotGothic16" => "DotGothic16",
            "Handjet" => "Handjet",
            "Rajdhani" => "Rajdhani",
            "Oxanium" => "Oxanium",
            "Kode Mono" => "Kode Mono",
            _ => "Pixelify Sans"
        };

        Resources["WidgetFontFamily"] = usesOriginalFont
            ? new System.Windows.Media.FontFamily("Segoe UI Variable Text")
            : new System.Windows.Media.FontFamily(
                new Uri("pack://application:,,,/"),
                $"./Assets/fonts/#{embeddedFontFamilyName}");

        Resources["MetricFontWeight"] = WidgetTextWeight switch
        {
            "Bold" => FontWeights.Bold,
            "SemiBold" => FontWeights.SemiBold,
            _ => FontWeights.Normal
        };
        Resources["ProviderCardBackground"] = usesRetroRendering
            ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xF2, 0x18, 0x1D, 0x24))
            : new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xE6, 0x2A, 0x2F, 0x38));
        Resources["ProviderCardBorderBrush"] = usesRetroRendering
            ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0x7F, 0x91, 0xA3))
            : System.Windows.Media.Brushes.Transparent;
        Resources["ProviderCardBorderThickness"] = new Thickness(usesRetroRendering ? 1 : 0);
        Resources["ProviderCardCornerRadius"] = new CornerRadius(usesRetroRendering ? 0 : 10);
        Resources["ProgressCornerRadius"] = new CornerRadius(usesRetroRendering ? 0 : 3);
        TextOptions.SetTextRenderingMode(
            this,
            usesRetroRendering ? TextRenderingMode.Aliased : TextRenderingMode.ClearType);
        TextOptions.SetTextHintingMode(
            this,
            usesRetroRendering ? TextHintingMode.Fixed : TextHintingMode.Auto);
    }

    private static string NormalizeWidgetFont(string? font) => font switch
    {
        "Segoe UI Variable Text" or
        "VT323" or
        "Pixelify Sans" or
        "Silkscreen" or
        "Tiny5" or
        "Space Mono" or
        "Chakra Petch" or
        "IBM Plex Mono" or
        "DotGothic16" or
        "Handjet" or
        "Rajdhani" or
        "Oxanium" or
        "Kode Mono" => font,
        _ => "Segoe UI Variable Text"
    };

    private static string NormalizeWidgetAppearance(string? appearance) =>
        appearance == "Retro" ? "Retro" : "Default";

    private static string NormalizeWidgetTextWeight(string? weight) => weight switch
    {
        "SemiBold" or "Bold" => weight,
        _ => "Regular"
    };

    private static string ExtractWidgetFont(string? combinedStyle) => combinedStyle switch
    {
        string value when value.StartsWith("VT323", StringComparison.Ordinal) => "VT323",
        string value when value.StartsWith("Pixelify Sans", StringComparison.Ordinal) => "Pixelify Sans",
        _ => "Segoe UI Variable Text"
    };

    private static string ExtractWidgetAppearance(string? combinedStyle) =>
        combinedStyle?.EndsWith(" - Retro", StringComparison.Ordinal) == true || combinedStyle == "Retro"
            ? "Retro"
            : "Default";

    public void SetAutoRefreshEnabled(bool enabled)
    {
        if (AutoRefreshEnabled == enabled)
        {
            return;
        }

        AutoRefreshEnabled = enabled;
        ApplyAutoRefreshOptions();
        SavePlacement();
    }

    public void SetRefreshInterval(ProviderKind provider, double minutes)
    {
        var normalized = AutoRefreshOptions.NormalizeInterval(minutes);
        if (provider == ProviderKind.Codex)
        {
            CodexRefreshIntervalMinutes = normalized;
        }
        else if (provider == ProviderKind.Claude)
        {
            ClaudeRefreshIntervalMinutes = normalized;
        }
        else if (provider == ProviderKind.Antigravity)
        {
            AntigravityRefreshIntervalMinutes = normalized;
        }
        else
        {
            CursorRefreshIntervalMinutes = normalized;
        }

        ApplyAutoRefreshOptions();
        SavePlacement();
    }

    public void SetIdleRefreshOptions(double idleAfterMinutes, double idleRefreshIntervalMinutes)
    {
        IdleAfterMinutes = AutoRefreshOptions.NormalizeInterval(idleAfterMinutes);
        IdleRefreshIntervalMinutes = AutoRefreshOptions.NormalizeInterval(idleRefreshIntervalMinutes);
        ApplyAutoRefreshOptions();
        SavePlacement();
    }

    public void SetThrottleInterval(ProviderKind provider, double minutes)
    {
        var normalized = AutoRefreshOptions.NormalizeThrottle(minutes);
        if (provider == ProviderKind.Codex)
        {
            CodexThrottleIntervalMinutes = normalized;
        }
        else if (provider == ProviderKind.Claude)
        {
            ClaudeThrottleIntervalMinutes = normalized;
        }
        else if (provider == ProviderKind.Antigravity)
        {
            AntigravityThrottleIntervalMinutes = normalized;
        }
        else
        {
            CursorThrottleIntervalMinutes = normalized;
        }

        ApplyThrottleOptions();
        SavePlacement();
    }

    public void ResetScheduledIntervalsToDefault()
    {
        CodexRefreshIntervalMinutes = AutoRefreshOptions.CodexDefaultIntervalMinutes;
        ClaudeRefreshIntervalMinutes = AutoRefreshOptions.ClaudeDefaultIntervalMinutes;
        AntigravityRefreshIntervalMinutes = AutoRefreshOptions.AntigravityDefaultIntervalMinutes;
        CursorRefreshIntervalMinutes = AutoRefreshOptions.CursorDefaultIntervalMinutes;
        IdleAfterMinutes = AutoRefreshOptions.DefaultIdleAfterMinutes;
        IdleRefreshIntervalMinutes = AutoRefreshOptions.DefaultIdleRefreshIntervalMinutes;
        ApplyAutoRefreshOptions();
        SavePlacement();
    }

    public void ResetThrottleIntervalsToDefault()
    {
        CodexThrottleIntervalMinutes = AutoRefreshOptions.CodexDefaultThrottleMinutes;
        ClaudeThrottleIntervalMinutes = AutoRefreshOptions.ClaudeDefaultThrottleMinutes;
        AntigravityThrottleIntervalMinutes = AutoRefreshOptions.AntigravityDefaultThrottleMinutes;
        CursorThrottleIntervalMinutes = AutoRefreshOptions.CursorDefaultThrottleMinutes;
        ApplyThrottleOptions();
        SavePlacement();
    }

    public void ResetUsageColorsToDefault()
    {
        TrySetUsageColors("#2ECC71", "#9ACD32", "#FFD21E", "#FF9800", "#FF4D4F", 29, 49, 69, 79, 84);
    }

    public void SetShowUsageRemaining(bool showRemaining)
    {
        if (ShowUsageRemaining == showRemaining)
        {
            return;
        }

        ShowUsageRemaining = showRemaining;
        _viewModel.SetUsageDisplayMode(showRemaining);
        SavePlacement();
    }

    private void ApplyAutoRefreshOptions() =>
        _autoRefreshOptions.Update(
            AutoRefreshEnabled,
            CodexRefreshIntervalMinutes,
            ClaudeRefreshIntervalMinutes,
            AntigravityRefreshIntervalMinutes,
            CursorRefreshIntervalMinutes,
            IdleAfterMinutes,
            IdleRefreshIntervalMinutes);

    private void ApplyThrottleOptions() =>
        _autoRefreshOptions.UpdateThrottle(
            CodexThrottleIntervalMinutes,
            ClaudeThrottleIntervalMinutes,
            AntigravityThrottleIntervalMinutes,
            CursorThrottleIntervalMinutes);

    public bool TrySetUsageColors(
        string green,
        string lime,
        string yellow,
        string orange,
        string red,
        double stage1Maximum,
        double stage2Maximum,
        double stage3Maximum,
        double stage4Maximum,
        double stage5Maximum)
    {
        if (!TryConfigureUsageColors(
                green,
                lime,
                yellow,
                orange,
                red,
                stage1Maximum,
                stage2Maximum,
                stage3Maximum,
                stage4Maximum,
                stage5Maximum))
        {
            return false;
        }

        GreenColorHex = green;
        LimeColorHex = lime;
        YellowColorHex = yellow;
        OrangeColorHex = orange;
        RedColorHex = red;
        Stage1MaxPercent = stage1Maximum;
        Stage2MaxPercent = stage2Maximum;
        Stage3MaxPercent = stage3Maximum;
        Stage4MaxPercent = stage4Maximum;
        Stage5MaxPercent = stage5Maximum;
        _viewModel.RefreshUsageColors();
        SavePlacement();
        return true;
    }

    private void ApplySavedUsageColors()
    {
        if (TryConfigureUsageColors(
                GreenColorHex,
                LimeColorHex,
                YellowColorHex,
                OrangeColorHex,
                RedColorHex,
                Stage1MaxPercent,
                Stage2MaxPercent,
                Stage3MaxPercent,
                Stage4MaxPercent,
                Stage5MaxPercent))
        {
            return;
        }

        GreenColorHex = "#2ECC71";
        LimeColorHex = "#9ACD32";
        YellowColorHex = "#FFD21E";
        OrangeColorHex = "#FF9800";
        RedColorHex = "#FF4D4F";
        Stage1MaxPercent = 29;
        Stage2MaxPercent = 49;
        Stage3MaxPercent = 69;
        Stage4MaxPercent = 79;
        Stage5MaxPercent = 84;
        TryConfigureUsageColors(
            GreenColorHex,
            LimeColorHex,
            YellowColorHex,
            OrangeColorHex,
            RedColorHex,
            Stage1MaxPercent,
            Stage2MaxPercent,
            Stage3MaxPercent,
            Stage4MaxPercent,
            Stage5MaxPercent);
    }

    private bool TryConfigureUsageColors(
        string green,
        string lime,
        string yellow,
        string orange,
        string red,
        double stage1Maximum,
        double stage2Maximum,
        double stage3Maximum,
        double stage4Maximum,
        double stage5Maximum) =>
        Resources["UsageColorConverter"] is UsageColorConverter converter &&
        converter.TryConfigure(
            green,
            lime,
            yellow,
            orange,
            red,
            stage1Maximum,
            stage2Maximum,
            stage3Maximum,
            stage4Maximum,
            stage5Maximum);

    private void ApplyFontSizePreset()
    {
        var typography = FontSizePreset switch
        {
            "Compact" => new WidgetTypography(8, 10, 6.5, 7.5, 14, 14, 10),
            "Small" => new WidgetTypography(9.5, 12.5, 8, 8.5, 17, 17, 12),
            "Large" => new WidgetTypography(15, 21, 13, 13, 27, 27, 18),
            "Extra Large" => new WidgetTypography(20, 30, 17, 16, 36, 36, 24),
            _ => new WidgetTypography(11.5, 15, 9.5, 10, 19, 19, 13)
        };

        Resources["ProviderFontSize"] = typography.ProviderFontSize;
        Resources["MetricFontSize"] = typography.MetricFontSize;
        Resources["ResetFontSize"] = typography.ResetFontSize;
        Resources["ActionFontSize"] = typography.ActionFontSize;
        Resources["ProviderHeaderHeight"] = typography.ProviderHeaderHeight;
        Resources["ProviderIconSize"] = typography.ProviderIconSize;
        Resources["MetricRowHeight"] = typography.MetricRowHeight;
        ApplyCompactHeight();
    }

    private void ApplyMetricLabelWidth() =>
        Resources["MetricLabelWidth"] = new GridLength(MetricLabelWidth);

    private void ApplyProgressBarHeight() => Resources["ProgressBarHeight"] = ProgressBarHeight;

    private static string NormalizeFontSizePreset(string? preset) => preset switch
    {
        "Compact" or "Small" or "Normal" or "Large" or "Extra Large" => preset,
        _ => "Normal"
    };

    private void ApplyProviderVisibility()
    {
        _viewModel.SetProviderVisibility(ProviderKind.Codex, ShowCodex);
        _viewModel.SetProviderVisibility(ProviderKind.Claude, ShowClaude);
        _viewModel.SetProviderVisibility(ProviderKind.Antigravity, ShowAntigravity);
        _viewModel.SetProviderVisibility(ProviderKind.Cursor, ShowCursor);
        ApplyProviderLayout();
    }

    private void ApplyProviderLayout()
    {
        // Provider count controls the compact widget's minimum width only. In dashboard mode
        // cards flow inside a fixed-width grid, so adding Antigravity/Cursor must not lock the
        // whole window at the compact horizontal layout's expanded minimum.
        if (IsDashboardLayoutEnabled)
        {
            MinWidth = DashboardMinWidth;
            MinHeight = DashboardMinHeight;
            return;
        }

        MinWidth = IsHorizontalLayout ? GetHorizontalMinWidth() : GetVerticalMinWidth();
        var compactItemsPanel = (ItemsPanelTemplate)FindResource(
            IsHorizontalLayout ? "HorizontalProvidersPanel" : "VerticalProvidersPanel");
        CompactItemsControl.ItemsPanel = compactItemsPanel;
        if (IsHorizontalLayout && Width < MinWidth)
        {
            Width = MinWidth;
        }

        ApplyCompactHeight();
    }

    private void ApplyCompactHeight()
    {
        // Dashboard mode manages its own window size (see ApplyDashboardLayoutMode) - content
        // there can be arranged and resized arbitrarily by the user, so auto-sizing the window to
        // "fit" it the way the compact view does would fight every manual resize/drag.
        if (IsDashboardLayoutEnabled)
        {
            return;
        }

        var providerHeights = _viewModel.Providers
            .Select(provider => 20d + Math.Max(1, provider.UsageWindows.Count) * 35d)
            .ToArray();
        var apiCostHeights = _viewModel.CodexApiCostPanels
            .Select(panel => panel.HasStatus ? 90d : 74d)
            .ToArray();
        var compactHeight = IsHorizontalLayout
            ? providerHeights.Concat(apiCostHeights).DefaultIfEmpty(60d).Max()
            : providerHeights.Sum() + apiCostHeights.Sum();

        compactHeight = Math.Ceiling(compactHeight * GetFontHeightScale());
        compactHeight = Math.Max(60, compactHeight);
        MinHeight = compactHeight;

        // Window.MinHeight only constrains manual drag-resizing at the OS level - it does not by
        // itself grow an already-open window, so the height must be assigned directly whenever the
        // compact content size changes (e.g. a Codex API Cost panel appearing/disappearing).
        if (Height != compactHeight)
        {
            Height = compactHeight;
        }
    }

    private double GetFontHeightScale() => FontSizePreset switch
    {
        "Compact" => 0.65,
        "Small" => 0.82,
        "Large" => 1.45,
        "Extra Large" => 1.95,
        _ => 1.0
    };

    private double GetHorizontalMinWidth()
    {
        var visibleWidgetCount = _viewModel.Providers.Count + _viewModel.CodexApiCostPanels.Count;
        if (visibleWidgetCount <= 1)
        {
            return GetVerticalMinWidth();
        }

        // Horizontal cards share a row, so they can be substantially narrower than the
        // standalone vertical widget. API-cost cards participate in the same calculation.
        return Math.Max(GetVerticalMinWidth(), GetHorizontalWidgetMinWidth() * visibleWidgetCount);
    }

    private double GetHorizontalWidgetMinWidth() => FontSizePreset switch
    {
        "Compact" => 80,
        "Small" => 90,
        "Large" => 140,
        "Extra Large" => 175,
        _ => 105
    };

    private double GetVerticalMinWidth() => FontSizePreset switch
    {
        "Compact" => 145,
        "Small" => 160,
        "Large" => 205,
        "Extra Large" => 235,
        _ => 180
    };

    private void WidgetContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        LockWindowMenuItem.Header = IsWindowLocked ? "Unlock widget" : "Lock widget";
        AlwaysOnTopMenuItem.IsChecked = AlwaysOnTop;
        HorizontalLayoutMenuItem.IsChecked = !IsDashboardLayoutEnabled && IsHorizontalLayout;
        VerticalLayoutMenuItem.IsChecked = !IsDashboardLayoutEnabled && !IsHorizontalLayout;
        DashboardLayoutMenuItem.IsChecked = IsDashboardLayoutEnabled;
        EditDashboardLayoutMenuItem.IsEnabled = IsDashboardLayoutEnabled;
        EditDashboardLayoutMenuItem.Header = _viewModel.DashboardLayout.IsEditMode
            ? "Done Editing Layout"
            : "Edit Layout";
        ResetDashboardLayoutMenuItem.IsEnabled = IsDashboardLayoutEnabled;
        foreach (var item in OpacityMenuItem.Items.OfType<System.Windows.Controls.MenuItem>())
        {
            item.IsCheckable = true;
            item.IsChecked = TryGetOpacity(item, out var opacity) && Math.Abs(Opacity - opacity) < 0.001;
        }
    }

    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e) =>
        _applicationController.ShowSettings();

    private async void RefreshAllMenuItem_Click(object sender, RoutedEventArgs e) =>
        await _applicationController.RefreshAllAsync();

    private void OpacityMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem item && TryGetOpacity(item, out var opacity))
        {
            Opacity = opacity;
            SavePlacement();
        }
    }

    private void HideWidgetMenuItem_Click(object sender, RoutedEventArgs e) =>
        SetDashboardWidgetVisible(false);

    private void LockWindowMenuItem_Click(object sender, RoutedEventArgs e) =>
        SetWindowLocked(!IsWindowLocked);

    private void AlwaysOnTopMenuItem_Click(object sender, RoutedEventArgs e) =>
        SetAlwaysOnTop(!AlwaysOnTop);

    private static bool TryGetOpacity(System.Windows.Controls.MenuItem item, out double opacity) =>
        double.TryParse(
            item.Tag?.ToString(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out opacity);

    private void ApplyWindowLockState() =>
        ResizeMode = IsWindowLocked ? ResizeMode.NoResize : ResizeMode.CanResize;

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource?.AddHook(WindowMessageHook);
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmNcHitTest || IsWindowLocked || ResizeMode == ResizeMode.NoResize)
        {
            return IntPtr.Zero;
        }

        var value = lParam.ToInt64();
        var screenPoint = new System.Windows.Point(
            unchecked((short)(value & 0xffff)),
            unchecked((short)((value >> 16) & 0xffff)));
        var point = PointFromScreen(screenPoint);
        const double resizeBorder = 7;
        var left = point.X >= 0 && point.X < resizeBorder;
        var right = point.X <= ActualWidth && point.X > ActualWidth - resizeBorder;
        var top = point.Y >= 0 && point.Y < resizeBorder;
        var bottom = point.Y <= ActualHeight && point.Y > ActualHeight - resizeBorder;

        var hitTest = (left, right, top, bottom) switch
        {
            (true, _, true, _) => HtTopLeft,
            (_, true, true, _) => HtTopRight,
            (true, _, _, true) => HtBottomLeft,
            (_, true, _, true) => HtBottomRight,
            (true, _, _, _) => HtLeft,
            (_, true, _, _) => HtRight,
            (_, _, true, _) => HtTop,
            (_, _, _, true) => HtBottom,
            _ => 0
        };

        if (hitTest == 0)
        {
            return IntPtr.Zero;
        }

        handled = true;
        return new IntPtr(hitTest);
    }

    private void SnapToBottomRight()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var workingArea = Forms.Screen.FromHandle(handle).WorkingArea;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
            ?? Matrix.Identity;
        var bottomRight = transform.Transform(new System.Windows.Point(workingArea.Right, workingArea.Bottom));
        const double margin = 10;
        Left = bottomRight.X - ActualWidth - margin;
        Top = bottomRight.Y - ActualHeight - margin;
    }

    private static WindowPlacement? ReadPlacement()
    {
        try
        {
            return File.Exists(PlacementPath)
                ? JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(PlacementPath))
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void SavePlacement()
    {
        if (!double.IsFinite(Left) || !double.IsFinite(Top))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PlacementPath)!);
            var temporaryPath = PlacementPath + ".tmp";
            var placement = new WindowPlacement
            {
                Left = Left,
                Top = Top,
                Width = ActualWidth,
                Height = ActualHeight,
                DashboardWidgetHeight = DashboardWidgetHeight,
                MetricLabelWidth = MetricLabelWidth,
                ProgressBarHeight = ProgressBarHeight,
                IsLocked = IsWindowLocked,
                IsDashboardLayoutEnabled = IsDashboardLayoutEnabled,
                IsHorizontalLayout = IsHorizontalLayout,
                ShowDashboardWidget = ShowDashboardWidget,
                AlwaysOnTop = AlwaysOnTop,
                ShowCodex = ShowCodex,
                ShowClaude = ShowClaude,
                ShowAntigravity = ShowAntigravity,
                ShowCursor = ShowCursor,
                FontSizePreset = FontSizePreset,
                WidgetFont = WidgetFont,
                WidgetAppearance = WidgetAppearance,
                WidgetTextWeight = WidgetTextWeight,
                WidgetStyle = $"{WidgetFont} - {WidgetAppearance}",
                GreenColorHex = GreenColorHex,
                LimeColorHex = LimeColorHex,
                YellowColorHex = YellowColorHex,
                OrangeColorHex = OrangeColorHex,
                RedColorHex = RedColorHex,
                Stage1MaxPercent = Stage1MaxPercent,
                Stage2MaxPercent = Stage2MaxPercent,
                Stage3MaxPercent = Stage3MaxPercent,
                Stage4MaxPercent = Stage4MaxPercent,
                Stage5MaxPercent = Stage5MaxPercent,
                ShowUsageRemaining = ShowUsageRemaining,
                AutoRefreshEnabled = AutoRefreshEnabled,
                CodexRefreshIntervalMinutes = CodexRefreshIntervalMinutes,
                ClaudeRefreshIntervalMinutes = ClaudeRefreshIntervalMinutes,
                AntigravityRefreshIntervalMinutes = AntigravityRefreshIntervalMinutes,
                CursorRefreshIntervalMinutes = CursorRefreshIntervalMinutes,
                IdleAfterMinutes = IdleAfterMinutes,
                IdleRefreshIntervalMinutes = IdleRefreshIntervalMinutes,
                CodexThrottleIntervalMinutes = CodexThrottleIntervalMinutes,
                ClaudeThrottleIntervalMinutes = ClaudeThrottleIntervalMinutes,
                AntigravityThrottleIntervalMinutes = AntigravityThrottleIntervalMinutes,
                CursorThrottleIntervalMinutes = CursorThrottleIntervalMinutes,
                Opacity = Opacity
            };
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(placement));
            File.Move(temporaryPath, PlacementPath, overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        // Every state setter routes through here, which makes this the one place that has to
        // notify Settings - see WidgetStateChanged.
        WidgetStateChanged?.Invoke();
    }

    private static bool IsOnVirtualDesktop(WindowPlacement placement)
    {
        if (!double.IsFinite(placement.Left) || !double.IsFinite(placement.Top))
        {
            return false;
        }

        var right = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
        var bottom = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
        return placement.Left >= SystemParameters.VirtualScreenLeft - 100 &&
               placement.Left < right &&
               placement.Top >= SystemParameters.VirtualScreenTop - 100 &&
               placement.Top < bottom;
    }

    private sealed class WindowPlacement
    {
        public double Left { get; init; }
        public double Top { get; init; }
        public double Width { get; init; } = 430;
        public double Height { get; init; } = 320;
        public double DashboardWidgetHeight { get; init; } = DefaultDashboardWidgetHeight;
        public double MetricLabelWidth { get; init; } = DefaultMetricLabelWidth;
        public double ProgressBarHeight { get; init; } = DefaultProgressBarHeight;
        public bool IsLocked { get; init; }
        public bool IsDashboardLayoutEnabled { get; init; } = true;
        public bool IsHorizontalLayout { get; init; }
        public bool ShowDashboardWidget { get; init; } = true;
        public bool AlwaysOnTop { get; init; } = true;
        public bool ShowCodex { get; init; } = true;
        public bool ShowClaude { get; init; } = true;
        public bool ShowAntigravity { get; init; } = true;
        public bool ShowCursor { get; init; } = true;
        public string FontSizePreset { get; init; } = "Large";
        public string? WidgetFont { get; init; } = "Oxanium";
        public string? WidgetAppearance { get; init; } = "Retro";
        public string? WidgetTextWeight { get; init; } = "Regular";
        public string? WidgetStyle { get; init; } = "Oxanium - Retro";
        public string GreenColorHex { get; init; } = "#2ECC71";
        public string LimeColorHex { get; init; } = "#9ACD32";
        public string YellowColorHex { get; init; } = "#FFD21E";
        public string OrangeColorHex { get; init; } = "#FF9800";
        public string RedColorHex { get; init; } = "#FF4D4F";
        public double Stage1MaxPercent { get; init; } = 29;
        public double Stage2MaxPercent { get; init; } = 49;
        public double Stage3MaxPercent { get; init; } = 69;
        public double Stage4MaxPercent { get; init; } = 79;
        public double Stage5MaxPercent { get; init; } = 84;
        public bool ShowUsageRemaining { get; init; }
        public bool AutoRefreshEnabled { get; init; } = true;
        public double CodexRefreshIntervalMinutes { get; init; } = AutoRefreshOptions.CodexDefaultIntervalMinutes;
        public double ClaudeRefreshIntervalMinutes { get; init; } = AutoRefreshOptions.ClaudeDefaultIntervalMinutes;
        public double AntigravityRefreshIntervalMinutes { get; init; } = AutoRefreshOptions.AntigravityDefaultIntervalMinutes;
        public double CursorRefreshIntervalMinutes { get; init; } = AutoRefreshOptions.CursorDefaultIntervalMinutes;
        public double IdleAfterMinutes { get; init; } = AutoRefreshOptions.DefaultIdleAfterMinutes;
        public double IdleRefreshIntervalMinutes { get; init; } = AutoRefreshOptions.DefaultIdleRefreshIntervalMinutes;
        public double CodexThrottleIntervalMinutes { get; init; } = AutoRefreshOptions.CodexDefaultThrottleMinutes;
        public double ClaudeThrottleIntervalMinutes { get; init; } = AutoRefreshOptions.ClaudeDefaultThrottleMinutes;
        public double AntigravityThrottleIntervalMinutes { get; init; } = AutoRefreshOptions.AntigravityDefaultThrottleMinutes;
        public double CursorThrottleIntervalMinutes { get; init; } = AutoRefreshOptions.CursorDefaultThrottleMinutes;
        public double Opacity { get; init; } = 1.0;
    }

    private sealed record WidgetTypography(
        double ProviderFontSize,
        double MetricFontSize,
        double ResetFontSize,
        double ActionFontSize,
        double ProviderHeaderHeight,
        double MetricRowHeight,
        double ProviderIconSize);
}
