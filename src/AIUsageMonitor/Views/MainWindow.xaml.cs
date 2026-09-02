using System.ComponentModel;
using System.Globalization;
using System.Collections.Specialized;
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
    // Only a floor for manual resizing - the height a dashboard actually opens at comes from its
    // grid (see FitDashboardPanelToContent). Kept near one grid row so a small board can size
    // down to its cards instead of carrying invisible window beneath them.
    private const double DashboardMinHeight = 60;
    private readonly IApplicationController _applicationController;
    private readonly MainViewModel _viewModel;
    // A Grid whose rows are all "*" and that sits inside a ScrollViewer (which measures its
    // content with infinite available height so it can decide whether to show a scrollbar) does
    // not divide its rows evenly - WPF falls back to auto-sizing each star row by its content's
    // desired size in that case, which is unpredictable once cards of different heights are
    // spanning different row combinations. Giving DashboardGrid an explicit total height (this
    // constant times the row count) instead makes every row exactly this many pixels tall,
    // always - which DashboardCard's drag/resize snapping math depends on to convert pixels back
    // to whole cell deltas.
    private readonly Dictionary<DashboardCardViewModel, DashboardCard> _dashboardElements = [];
    private readonly DashboardWidgetSettings _settings;
    private HwndSource? _windowSource;
    private bool _isDraggingWindow;
    private bool _closingForHide;
    private bool _isClosed;
    private System.Windows.Point _dragStartPosition;

    public bool IsWindowLocked => _settings.IsWindowLocked;
    public bool IsDashboardLayoutEnabled => _settings.IsDashboardLayoutEnabled;
    public double DashboardWidgetHeight => _settings.DashboardWidgetHeight;
    public double MetricLabelWidth => _settings.MetricLabelWidth;
    public double ProgressBarHeight => _settings.ProgressBarHeight;
    public bool IsHorizontalLayout => _settings.IsHorizontalLayout;
    public bool ShowDashboardWidget => _settings.ShowDashboardWidget;
    public bool AlwaysOnTop => _settings.AlwaysOnTop;
    public bool ShowCodex => _settings.ShowCodex;
    public bool ShowClaude => _settings.ShowClaude;
    public bool ShowAntigravity => _settings.ShowAntigravity;
    public bool ShowCursor => _settings.ShowCursor;
    public string FontSizePreset => _settings.FontSizePreset;
    public string WidgetFont => _settings.WidgetFont;
    public string WidgetAppearance => _settings.WidgetAppearance;
    public string WidgetTextWeight => _settings.WidgetTextWeight;
    public string GreenColorHex => _settings.GreenColorHex;
    public string LimeColorHex => _settings.LimeColorHex;
    public string YellowColorHex => _settings.YellowColorHex;
    public string OrangeColorHex => _settings.OrangeColorHex;
    public string RedColorHex => _settings.RedColorHex;
    public double Stage1MaxPercent => _settings.Stage1MaxPercent;
    public double Stage2MaxPercent => _settings.Stage2MaxPercent;
    public double Stage3MaxPercent => _settings.Stage3MaxPercent;
    public double Stage4MaxPercent => _settings.Stage4MaxPercent;
    public double Stage5MaxPercent => _settings.Stage5MaxPercent;
    public bool ShowUsageRemaining => _settings.ShowUsageRemaining;
    public bool AutoRefreshEnabled => _settings.AutoRefreshEnabled;
    public double CodexRefreshIntervalMinutes => _settings.CodexRefreshIntervalMinutes;
    public double ClaudeRefreshIntervalMinutes => _settings.ClaudeRefreshIntervalMinutes;
    public double AntigravityRefreshIntervalMinutes => _settings.AntigravityRefreshIntervalMinutes;
    public double CursorRefreshIntervalMinutes => _settings.CursorRefreshIntervalMinutes;
    public double IdleAfterMinutes => _settings.IdleAfterMinutes;
    public double IdleRefreshIntervalMinutes => _settings.IdleRefreshIntervalMinutes;
    public double CodexThrottleIntervalMinutes => _settings.CodexThrottleIntervalMinutes;
    public double ClaudeThrottleIntervalMinutes => _settings.ClaudeThrottleIntervalMinutes;
    public double AntigravityThrottleIntervalMinutes => _settings.AntigravityThrottleIntervalMinutes;
    public double CursorThrottleIntervalMinutes => _settings.CursorThrottleIntervalMinutes;

    public MainWindow(
        MainViewModel viewModel,
        IApplicationController applicationController,
        DashboardWidgetSettings settings)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
        _applicationController = applicationController;
        _settings = settings;
        _settings.Changed += DashboardWidgetSettings_Changed;
        _viewModel.LayoutChanged += MainViewModel_LayoutChanged;
        _viewModel.DashboardLayout.Cards.CollectionChanged += DashboardCards_CollectionChanged;
        _viewModel.DashboardLayout.PropertyChanged += DashboardLayout_PropertyChanged;
        DashboardGrid.SizeChanged += (_, _) => RenderDashboardGridLines();
        Loaded += MainWindow_Loaded;
        SourceInitialized += MainWindow_SourceInitialized;

        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is not true)
            {
                return;
            }

            // Sizing needs a laid-out ScrollViewer, which a hidden window has no reason to keep
            // up to date - so a card that appeared or disappeared while the widget was hidden is
            // only reflected in its height now.
            FitDashboardPanelToContent();

            // Showing the widget again from the tray is also the moment Windows would quietly
            // move a window that is off the display; keep our saved position in step with it.
            if (EnsureOnScreen())
            {
                SavePlacement();
            }
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SavePlacement();
        if (!_closingForHide && System.Windows.Application.Current is App { IsExiting: false })
        {
            e.Cancel = true;
            _ = _applicationController.ExitAsync();
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        _settings.Changed -= DashboardWidgetSettings_Changed;
        _viewModel.LayoutChanged -= MainViewModel_LayoutChanged;
        _viewModel.DashboardLayout.Cards.CollectionChanged -= DashboardCards_CollectionChanged;
        _viewModel.DashboardLayout.PropertyChanged -= DashboardLayout_PropertyChanged;
        foreach (var card in _dashboardElements.Keys)
        {
            card.PropertyChanged -= DashboardCard_PropertyChanged;
        }

        _dashboardElements.Clear();
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
        base.OnClosed(e);
    }

    public void CloseForHide()
    {
        _closingForHide = true;
        Close();
    }

    private void MainViewModel_LayoutChanged() => QueueVisualRefresh(ApplyProviderLayout);

    private void DashboardCards_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        QueueVisualRefresh(RebuildDashboardGrid);

    private void DashboardWidgetSettings_Changed() => QueueVisualRefresh(ApplySettingsToWindow);

    private void QueueVisualRefresh(Action action)
    {
        if (_isClosed)
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!_isClosed)
                {
                    action();
                }
            });
        }
    }

    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsWindowLocked || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        // Raised here rather than only on drag-start: WS_EX_NOACTIVATE (see
        // TaskbarInterop.ApplyNonActivatingToolWindowStyle) means this window is barred from ever
        // activating, so any click - even one landing on a button rather than starting a drag -
        // still needs an explicit z-order nudge. Keep an always-on-top widget in the topmost band;
        // RaiseZOrderWithoutActivating deliberately ends in the non-topmost band and would make
        // the widget coverable immediately after a move while leaving the setting checked.
        var handle = new WindowInteropHelper(this).Handle;
        if (AlwaysOnTop)
        {
            TaskbarInterop.ForceTopMost(handle);
        }
        else
        {
            TaskbarInterop.RaiseZOrderWithoutActivating(handle);
        }

        if (IsInsideButton(e.OriginalSource as DependencyObject) ||
            IsInsideThumb(e.OriginalSource as DependencyObject))
        {
            return;
        }

        _isDraggingWindow = true;
        _dragStartPosition = e.GetPosition(this);
        Mouse.Capture(this);
        e.Handled = true;
    }

    private void Window_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDraggingWindow)
        {
            return;
        }

        // Position is relative to the window itself, so it stays a fixed reference point
        // (_dragStartPosition) throughout the drag - only the delta since the last move needs to
        // be applied to Left/Top. Mixing this with screen-space coordinates causes runaway
        // feedback since Left/Top would be included on both sides of the update.
        var currentPosition = e.GetPosition(this);
        Left += currentPosition.X - _dragStartPosition.X;
        Top += currentPosition.Y - _dragStartPosition.Y;
    }

    private void Window_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingWindow)
        {
            return;
        }

        EndWindowDrag();
        e.Handled = true;
    }

    private void Window_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e) => EndWindowDrag();

    private void EndWindowDrag()
    {
        if (!_isDraggingWindow)
        {
            return;
        }

        _isDraggingWindow = false;
        Mouse.Capture(null);
        SavePlacement();
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
        if (_settings.HasSavedPlacement && IsOnVirtualDesktop(_settings))
        {
            Left = _settings.Left;
            Top = _settings.Top;
            if (double.IsFinite(_settings.Width) && _settings.Width >= MinWidth)
            {
                Width = _settings.Width;
            }

            // Height is restored only for the compact view, which owns it. In Dashboard Layout
            // the height is not user state at all - it is the grid's row count times the
            // configured row height, recomputed by FitDashboardPanelToContent below. Deriving it
            // every start rather than trusting the file means a height measured at a bad moment
            // lasts until the next fit instead of persisting forever.
            if (!_settings.IsDashboardLayoutEnabled &&
                double.IsFinite(_settings.Height) &&
                _settings.Height >= MinHeight)
            {
                Height = _settings.Height;
            }
        }
        else
        {
            Dispatcher.BeginInvoke(() =>
            {
                SnapToBottomRight();
                SavePlacement();
            }, DispatcherPriority.ContextIdle);
        }

        ApplySettingsToWindow();

        // Run once the restored size has actually been applied. A placement can be outside the
        // work area through no fault of this session - saved before this check existed, or saved
        // on a monitor arrangement that has since changed - so correct it on the way in rather
        // than leaving Windows to do it at some unrelated moment later.
        Dispatcher.BeginInvoke(() =>
        {
            if (EnsureOnScreen())
            {
                SavePlacement();
            }
        }, DispatcherPriority.ContextIdle);
    }

    public void SetWindowLocked(bool isLocked)
        => _settings.SetWindowLocked(isLocked);

    public void SetHorizontalLayout(bool isHorizontal)
        => _settings.SetHorizontalLayout(isHorizontal);

    public void SetDashboardWidgetVisible(bool isVisible)
    {
        if (isVisible)
        {
            _applicationController.ShowMainWindow();
            return;
        }

        _applicationController.HideMainWindow();
    }

    public void SetDashboardLayoutEnabled(bool enabled)
    {
        if (!enabled)
        {
            _viewModel.DashboardLayout.IsEditMode = false;
        }

        _settings.SetDashboardLayoutEnabled(enabled);
    }

    public void SetDashboardWidgetHeight(double height)
        => _settings.SetDashboardWidgetHeight(height);

    public void SetMetricLabelWidth(double width)
        => _settings.SetMetricLabelWidth(width);

    public void SetProgressBarHeight(double height)
        => _settings.SetProgressBarHeight(height);

    private void ApplySettingsToWindow()
    {
        if (_isClosed || !IsLoaded)
        {
            return;
        }

        ApplyWindowLockState();
        ApplySavedUsageColors();
        ApplyWidgetPresentation();
        ApplyFontSizePreset();
        ApplyMetricLabelWidth();
        ApplyProgressBarHeight();
        ApplyProviderLayout();
        ApplyDashboardLayoutMode();
        ApplyDashboardEditModeVisuals();
        Topmost = AlwaysOnTop;
        Opacity = _settings.Opacity;

        if (IsDashboardLayoutEnabled)
        {
            RebuildDashboardGrid();
            Dispatcher.BeginInvoke(() =>
            {
                if (!_isClosed)
                {
                    RebuildDashboardGrid();
                    FitDashboardPanelToContent();
                }
            }, DispatcherPriority.ContextIdle);
        }
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

        RebuildDashboardGrid();
        ApplyDashboardGridOverlaySize();

        // Height is not seeded from a constant the way Width is: the grid knows exactly how tall
        // it needs to be, so let it say - a two-row dashboard has no business opening 480 tall
        // with the bottom half of the window empty and transparent.
        Dispatcher.BeginInvoke(FitDashboardPanelToContent, DispatcherPriority.ContextIdle);
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
                    // The row count is the grid's height, so the window follows it - including
                    // downwards, when rows are packed away again (see CompactEmptyRows).
                    FitDashboardPanelToContent();
                }, DispatcherPriority.ContextIdle);
                break;
            case nameof(DashboardLayoutViewModel.IsEditMode):
                Dispatcher.BeginInvoke(() =>
                {
                    ApplyDashboardEditModeVisuals();

                    // Both directions: entering edit mode has just added the spare parking rows
                    // and the window has to grow to show them, leaving it packs them away again
                    // and the window gives the space back.
                    FitDashboardPanelToContent();
                }, DispatcherPriority.ContextIdle);
                break;
        }
    }

    // Sizes the window to exactly the height of the dashboard grid - in BOTH directions. Growing
    // stops the ScrollViewer from hiding the bottom cards; shrinking matters just as much,
    // because the window is transparent: rows the grid no longer needs (Edit Layout's spare
    // parking rows, or a card that has been hidden) would otherwise stay as invisible window
    // below the last card. That dead space cannot be seen, only felt - it is what stops the
    // widget being dragged any lower once its bottom edge reaches the taskbar.
    private void FitDashboardPanelToContent()
    {
        // Only measure a window that is actually laid out. A hidden or not-yet-loaded window can
        // report a viewport that has nothing to do with what the user will see, and acting on
        // that would resize the widget from a meaningless number. Becoming visible re-runs this.
        if (!IsDashboardLayoutEnabled || !IsLoaded || !IsVisible)
        {
            return;
        }

        DashboardScrollViewer.UpdateLayout();
        var viewport = DashboardScrollViewer.ViewportHeight;
        var content = DashboardScrollViewer.ExtentHeight;
        if (viewport <= 0 || content <= 0)
        {
            return;
        }

        // Positive = cards are being clipped, negative = window is taller than its content.
        var difference = content - viewport;
        if (Math.Abs(difference) <= 0.5)
        {
            return;
        }

        Height = Math.Max(MinHeight, Height + Math.Ceiling(difference) + (difference > 0 ? 2 : 0));

        // Growing only ever pushes the bottom edge down, so a widget already sitting low on the
        // screen can end up hanging off it - see EnsureOnScreen. Position is worth saving;
        // the height deliberately is not (see MainWindow_Loaded).
        if (EnsureOnScreen())
        {
            SavePlacement();
        }

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
        => _settings.SetAlwaysOnTop(alwaysOnTop);

    public void SetProviderVisibility(ProviderKind provider, bool isVisible)
        => _settings.SetProviderVisibility(provider, isVisible);

    public void SetFontSizePreset(string preset)
        => _settings.SetFontSizePreset(preset);

    public void SetWidgetFont(string font)
        => _settings.SetWidgetFont(font);

    public void SetWidgetAppearance(string appearance)
        => _settings.SetWidgetAppearance(appearance);

    public void SetWidgetTextWeight(string weight)
        => _settings.SetWidgetTextWeight(weight);

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

    public void SetAutoRefreshEnabled(bool enabled)
        => _settings.SetAutoRefreshEnabled(enabled);

    public void SetRefreshInterval(ProviderKind provider, double minutes)
        => _settings.SetRefreshInterval(provider, minutes);

    public void SetIdleRefreshOptions(double idleAfterMinutes, double idleRefreshIntervalMinutes)
        => _settings.SetIdleRefreshOptions(idleAfterMinutes, idleRefreshIntervalMinutes);

    public void SetThrottleInterval(ProviderKind provider, double minutes)
        => _settings.SetThrottleInterval(provider, minutes);

    public void ResetScheduledIntervalsToDefault()
        => _settings.ResetScheduledIntervalsToDefault();

    public void ResetThrottleIntervalsToDefault()
        => _settings.ResetThrottleIntervalsToDefault();

    public void ResetUsageColorsToDefault()
        => _settings.ResetUsageColorsToDefault();

    public void SetShowUsageRemaining(bool showRemaining)
        => _settings.SetShowUsageRemaining(showRemaining);

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
        return _settings.TrySetUsageColors(
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
    }

    private void ApplySavedUsageColors()
    {
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

            // Compact content grows the window downwards as usage windows arrive, which can push
            // a widget parked low on the screen off the bottom of it - see EnsureOnScreen.
            if (EnsureOnScreen())
            {
                SavePlacement();
            }
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
            _settings.SetOpacity(opacity);
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
        var handle = new WindowInteropHelper(this).Handle;
        TaskbarInterop.ApplyNonActivatingToolWindowStyle(handle);
        _windowSource = HwndSource.FromHwnd(handle);
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

    // Keeps the widget on the screen it is on - screen *bounds*, deliberately not the work area,
    // so parking it against or partly beneath the taskbar stays possible. This only rescues a
    // widget that has ended up genuinely off the display, which Windows would otherwise do at the
    // next display change, wake or resolution switch - and because that correction is never
    // saved, the widget reappears somewhere other than where it was left, which reads as the app
    // moving on its own. Doing it here means the correction is at least persisted.
    //
    // Not called for user drags or resizes: where the user puts it is where it stays.
    // Callers save when this returns true.
    private bool EnsureOnScreen()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !double.IsFinite(Left) || !double.IsFinite(Top))
        {
            return false;
        }

        var bounds = Forms.Screen.FromHandle(handle).Bounds;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
            ?? Matrix.Identity;
        var topLeft = transform.Transform(new System.Windows.Point(bounds.Left, bounds.Top));
        var bottomRight = transform.Transform(new System.Windows.Point(bounds.Right, bounds.Bottom));

        var width = double.IsFinite(Width) ? Width : ActualWidth;
        var height = double.IsFinite(Height) ? Height : ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var changed = false;

        // A widget larger than the screen can never be fully placed on it. Trim it to fit
        // first - the dashboard's ScrollViewer keeps the overflowing cards reachable.
        if (height > bottomRight.Y - topLeft.Y)
        {
            height = Math.Max(MinHeight, bottomRight.Y - topLeft.Y);
            Height = height;
            changed = true;
        }

        if (width > bottomRight.X - topLeft.X)
        {
            width = Math.Max(MinWidth, bottomRight.X - topLeft.X);
            Width = width;
            changed = true;
        }

        var left = Math.Clamp(Left, topLeft.X, Math.Max(topLeft.X, bottomRight.X - width));
        var top = Math.Clamp(Top, topLeft.Y, Math.Max(topLeft.Y, bottomRight.Y - height));

        if (Math.Abs(left - Left) > 0.5)
        {
            Left = left;
            changed = true;
        }

        if (Math.Abs(top - Top) > 0.5)
        {
            Top = top;
            changed = true;
        }

        return changed;
    }

    private void SavePlacement()
    {
        _settings.UpdateWindowPlacement(Left, Top, ActualWidth, ActualHeight);
    }

    private static bool IsOnVirtualDesktop(DashboardWidgetSettings placement)
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

    private sealed record WidgetTypography(
        double ProviderFontSize,
        double MetricFontSize,
        double ResetFontSize,
        double ActionFontSize,
        double ProviderHeaderHeight,
        double MetricRowHeight,
        double ProviderIconSize);
}
