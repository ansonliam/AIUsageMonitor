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
    private static readonly string PlacementPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AIUsageMonitor",
        "window-placement.json");
    private readonly IApplicationController _applicationController;
    private readonly AutoRefreshOptions _autoRefreshOptions;
    private readonly MainViewModel _viewModel;
    private HwndSource? _windowSource;

    public bool IsWindowLocked { get; private set; }
    public bool IsHorizontalLayout { get; private set; }
    public bool ShowCodex { get; private set; } = true;
    public bool ShowClaude { get; private set; } = true;
    public bool ShowAntigravity { get; private set; } = true;
    public string FontSizePreset { get; private set; } = "Normal";
    public string GreenColorHex { get; private set; } = "#2ECC71";
    public string LimeColorHex { get; private set; } = "#9ACD32";
    public string YellowColorHex { get; private set; } = "#FFD21E";
    public string OrangeColorHex { get; private set; } = "#FF9800";
    public string RedColorHex { get; private set; } = "#FF4D4F";
    public double Stage1MaxPercent { get; private set; } = 40;
    public double Stage2MaxPercent { get; private set; } = 70;
    public double Stage3MaxPercent { get; private set; } = 85;
    public double Stage4MaxPercent { get; private set; } = 95;
    public double Stage5MaxPercent { get; private set; } = 100;
    public bool AutoRefreshEnabled { get; private set; }
    public double CodexRefreshIntervalMinutes { get; private set; } = AutoRefreshOptions.DefaultIntervalMinutes;
    public double ClaudeRefreshIntervalMinutes { get; private set; } = AutoRefreshOptions.DefaultIntervalMinutes;
    public double AntigravityRefreshIntervalMinutes { get; private set; } = AutoRefreshOptions.DefaultIntervalMinutes;

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
        if (IsWindowLocked || e.ChangedButton != MouseButton.Left || IsInsideButton(e.OriginalSource as DependencyObject))
        {
            return;
        }

        DragMove();
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
            IsHorizontalLayout = placement.IsHorizontalLayout;
            ShowCodex = placement.ShowCodex;
            ShowClaude = placement.ShowClaude;
            ShowAntigravity = placement.ShowAntigravity;
            FontSizePreset = NormalizeFontSizePreset(placement.FontSizePreset);
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
            AutoRefreshEnabled = placement.AutoRefreshEnabled;
            CodexRefreshIntervalMinutes = AutoRefreshOptions.NormalizeInterval(
                placement.CodexRefreshIntervalMinutes);
            ClaudeRefreshIntervalMinutes = AutoRefreshOptions.NormalizeInterval(
                placement.ClaudeRefreshIntervalMinutes);
            AntigravityRefreshIntervalMinutes = AutoRefreshOptions.NormalizeInterval(
                placement.AntigravityRefreshIntervalMinutes);
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
        ApplySavedUsageColors();
        ApplyFontSizePreset();
        ApplyProviderLayout();
        ApplyProviderVisibility();
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
        else
        {
            ShowAntigravity = isVisible;
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
        else
        {
            AntigravityRefreshIntervalMinutes = normalized;
        }

        ApplyAutoRefreshOptions();
        SavePlacement();
    }

    private void ApplyAutoRefreshOptions() =>
        _autoRefreshOptions.Update(
            AutoRefreshEnabled,
            CodexRefreshIntervalMinutes,
            ClaudeRefreshIntervalMinutes,
            AntigravityRefreshIntervalMinutes);

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
        Stage1MaxPercent = 40;
        Stage2MaxPercent = 70;
        Stage3MaxPercent = 85;
        Stage4MaxPercent = 95;
        Stage5MaxPercent = 100;
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
            "Compact" => new WidgetTypography(8, 10, 6.5, 7.5, 14, 14),
            "Small" => new WidgetTypography(9.5, 12.5, 8, 8.5, 17, 17),
            "Large" => new WidgetTypography(15, 21, 13, 13, 27, 27),
            "Extra Large" => new WidgetTypography(20, 30, 17, 16, 36, 36),
            _ => new WidgetTypography(11.5, 15, 9.5, 10, 19, 19)
        };

        Resources["ProviderFontSize"] = typography.ProviderFontSize;
        Resources["MetricFontSize"] = typography.MetricFontSize;
        Resources["ResetFontSize"] = typography.ResetFontSize;
        Resources["ActionFontSize"] = typography.ActionFontSize;
        Resources["ProviderHeaderHeight"] = typography.ProviderHeaderHeight;
        Resources["MetricRowHeight"] = typography.MetricRowHeight;
        ApplyCompactHeight();
    }

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
        ApplyProviderLayout();
    }

    private void ApplyProviderLayout()
    {
        MinWidth = IsHorizontalLayout ? GetHorizontalMinWidth() : GetVerticalMinWidth();
        ProvidersItemsControl.ItemsPanel = (ItemsPanelTemplate)FindResource(
            IsHorizontalLayout ? "HorizontalProvidersPanel" : "VerticalProvidersPanel");
        if (IsHorizontalLayout && Width < MinWidth)
        {
            Width = MinWidth;
        }

        ApplyCompactHeight();
    }

    private void ApplyCompactHeight()
    {
        var providerHeights = _viewModel.Providers
            .Select(provider => 20d + Math.Max(1, provider.UsageWindows.Count) * 35d)
            .ToArray();
        var compactHeight = providerHeights.Length == 0
            ? 60d
            : IsHorizontalLayout
                ? providerHeights.Max()
                : providerHeights.Sum();
        compactHeight = Math.Ceiling(compactHeight * GetFontHeightScale());
        compactHeight = Math.Max(60, compactHeight);
        MinHeight = compactHeight;
        if (Height > compactHeight)
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
        var visibleProviderCount =
            (ShowCodex ? 1 : 0) +
            (ShowClaude ? 1 : 0) +
            (ShowAntigravity ? 1 : 0);
        if (visibleProviderCount <= 1)
        {
            return GetVerticalMinWidth();
        }

        return GetVerticalMinWidth() * visibleProviderCount;
    }

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
        LockWindowMenuItem.Header = IsWindowLocked ? "Unlock window" : "Lock window";
        foreach (var item in OpacityMenuItem.Items.OfType<System.Windows.Controls.MenuItem>())
        {
            item.IsCheckable = true;
            item.IsChecked = TryGetOpacity(item, out var opacity) && Math.Abs(Opacity - opacity) < 0.001;
        }

        foreach (var item in FontSizeMenuItem.Items.OfType<System.Windows.Controls.MenuItem>())
        {
            item.IsCheckable = true;
            item.IsChecked = string.Equals(item.Tag?.ToString(), FontSizePreset, StringComparison.Ordinal);
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

    private void FontSizeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem item && item.Tag is string preset)
        {
            SetFontSizePreset(preset);
        }
    }

    private void LockWindowMenuItem_Click(object sender, RoutedEventArgs e) =>
        SetWindowLocked(!IsWindowLocked);

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
                IsLocked = IsWindowLocked,
                IsHorizontalLayout = IsHorizontalLayout,
                ShowCodex = ShowCodex,
                ShowClaude = ShowClaude,
                ShowAntigravity = ShowAntigravity,
                FontSizePreset = FontSizePreset,
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
                AutoRefreshEnabled = AutoRefreshEnabled,
                CodexRefreshIntervalMinutes = CodexRefreshIntervalMinutes,
                ClaudeRefreshIntervalMinutes = ClaudeRefreshIntervalMinutes,
                AntigravityRefreshIntervalMinutes = AntigravityRefreshIntervalMinutes,
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
        public double Width { get; init; } = 302;
        public double Height { get; init; } = 230;
        public bool IsLocked { get; init; }
        public bool IsHorizontalLayout { get; init; }
        public bool ShowCodex { get; init; } = true;
        public bool ShowClaude { get; init; } = true;
        public bool ShowAntigravity { get; init; } = true;
        public string FontSizePreset { get; init; } = "Normal";
        public string GreenColorHex { get; init; } = "#2ECC71";
        public string LimeColorHex { get; init; } = "#9ACD32";
        public string YellowColorHex { get; init; } = "#FFD21E";
        public string OrangeColorHex { get; init; } = "#FF9800";
        public string RedColorHex { get; init; } = "#FF4D4F";
        public double Stage1MaxPercent { get; init; } = 40;
        public double Stage2MaxPercent { get; init; } = 70;
        public double Stage3MaxPercent { get; init; } = 85;
        public double Stage4MaxPercent { get; init; } = 95;
        public double Stage5MaxPercent { get; init; } = 100;
        public bool AutoRefreshEnabled { get; init; }
        public double CodexRefreshIntervalMinutes { get; init; } = AutoRefreshOptions.DefaultIntervalMinutes;
        public double ClaudeRefreshIntervalMinutes { get; init; } = AutoRefreshOptions.DefaultIntervalMinutes;
        public double AntigravityRefreshIntervalMinutes { get; init; } = AutoRefreshOptions.DefaultIntervalMinutes;
        public double Opacity { get; init; } = 1.0;
    }

    private sealed record WidgetTypography(
        double ProviderFontSize,
        double MetricFontSize,
        double ResetFontSize,
        double ActionFontSize,
        double ProviderHeaderHeight,
        double MetricRowHeight);
}
