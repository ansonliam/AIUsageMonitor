using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using AIUsageMonitor.Converters;
using AIUsageMonitor.Models;
using AIUsageMonitor.Services;
using AIUsageMonitor.ViewModels;

namespace AIUsageMonitor.Views;

public partial class TaskbarWidgetWindow : Window
{
    // Backstop that guarantees recovery. It re-asserts topmost unconditionally (a single cheap
    // SetWindowPos) rather than only when position/size look wrong, because being *covered* by
    // the taskbar changes neither our position nor our size - a conditional check can't detect it
    // at all, which would otherwise leave the widget hidden indefinitely.
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(10);
    private const double PositionToleranceDip = 1.0;
    private const double DefaultSecondaryRightOffset = 120;

    // Explorer re-asserts its own topmost slot as part of taskbar interaction, and can do so a
    // moment AFTER we re-assert ours - whichever SetWindowPos(HWND_TOPMOST) call lands last wins
    // the top of the band. Because the taskbar jumping above us doesn't move our window, Windows
    // sends us no message about it (there is no notification when a sibling jumps above you), so
    // a single re-assert per event silently loses that ordering race. Re-asserting a few times
    // across Explorer's settling window covers it without resorting to continuous polling.
    // Interval is deliberately short: since each check is a conditional no-op when nothing is
    // covering us (see ReassertTopMost), the only real cost of checking more often is two cheap
    // reads - and the payoff is directly shorter visible blink, because the widget stays covered
    // only until the next check notices. 10 ticks keeps ~500ms of coverage for a late settle.
    private static readonly TimeSpan SettleInterval = TimeSpan.FromMilliseconds(50);
    private const int SettleTickCount = 10;

    // Win+Tab (Task View) is NOT recoverable by any amount of re-asserting, and the settle
    // deliberately does not try. Measured: pressing Win+Tab drops the widget behind the taskbar
    // ~200ms before the overlay even appears, and it stays there until the overlay is dismissed
    // (~2.7s observed). Holding the settle open and contesting the slot at 20Hz for the whole
    // duration was tried and changed nothing, which points at Explorer raising the taskbar into a
    // z-band above normal topmost for the duration - SetWindowPos(HWND_TOPMOST) cannot cross a
    // band boundary, so there is no retry rate that wins. Left alone on purpose: the visible
    // result (taskbar showing without the widget) is identical to hiding for Task View, so
    // neither contesting it nor hiding is worth the code.

    // Nothing user-driven can reorder windows while there has been no input at all, so the
    // watchdog skips its checks entirely once idle. Much shorter than AutoRefreshOptions' own
    // 5-minute idle notion, which exists for a different purpose (how often to poll provider
    // APIs) - here it only needs to be long enough to outlast a pause between clicks.
    private static readonly TimeSpan IdleThreshold = TimeSpan.FromSeconds(30);

    private readonly TaskbarWidgetViewModel _viewModel;
    private readonly TaskbarWidgetSettingsStore _settingsStore;
    private readonly TaskbarWidgetPositioningService _positioningService;
    private readonly TaskbarMonitorService _monitorService;
    private readonly TaskbarWidgetWindow? _owner;
    private readonly string? _monitorId;
    private readonly Dictionary<string, TaskbarWidgetWindow> _secondaryWindows = [];
    private readonly HashSet<string> _enabledMonitorIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TaskbarMonitorAppearanceSettings> _monitorAppearances = new(StringComparer.OrdinalIgnoreCase);
    // Only needed to force a redraw of every UsageMetricViewModel's colour after the shared
    // usage-colour-stage settings are applied - see TrySetUsageColors.
    private readonly MainViewModel _mainViewModel;
    private readonly IApplicationController _applicationController;
    private readonly ISystemIdleTimeProvider _idleTimeProvider;
    private readonly DispatcherTimer _watchdogTimer;
    private readonly DispatcherTimer _settleTimer;
    private int _settleTicksRemaining;
    private bool _wasIdle;
    // True while a fullscreen app covers this widget's monitor. The widget hides for the duration
    // rather than trying to sit below the app: it has to be in the topmost band to clear the
    // taskbar at all, and a fullscreen app is normally NOT topmost, so there is no z-order slot
    // that is above the taskbar and below the app - see TaskbarInterop.IsMonitorCoveredByFullscreenWindow.
    private bool _isFullscreenActive;
    // EVENT_OBJECT_LOCATIONCHANGE fires continuously while a window is dragged or resized, so the
    // re-check is coalesced to one queued dispatch rather than one per event.
    private bool _fullscreenCheckQueued;
    // Kept alive for the hook's lifetime - SetWinEventHook only stores a native function pointer
    // to the delegate, so letting it be collected would leave the hook calling into freed memory.
    private readonly TaskbarInterop.WinEventDelegate _foregroundChangedHandler;
    private readonly TaskbarInterop.WinEventDelegate _taskbarLocationChangedHandler;
    private IntPtr _foregroundHook = IntPtr.Zero;
    private IntPtr _locationHook = IntPtr.Zero;
    private IntPtr _cachedTaskbarHandle = IntPtr.Zero;
    // The tray icon cluster (TrayNotifyWnd) can resize independently of the taskbar's own outer
    // bounds - e.g. reorganizing which icons are hidden/shown changes its width without Shell_
    // TrayWnd's rect changing at all, so it needs its own cached handle to watch.
    private IntPtr _cachedTrayNotifyHandle = IntPtr.Zero;
    private uint _taskbarCreatedMessage;
    private HwndSource? _windowSource;

    // Same purpose as MainWindow.WidgetStateChanged: Settings is non-modal, and this widget's own
    // context menu can change state (Hide) while it is open.
    public event Action? WidgetStateChanged;

    public bool ShowTaskbarWidget { get; private set; } = true;
    public bool SyncTaskbarMonitorAppearance { get; private set; }
    public bool ShowCodexOnTaskbar { get; private set; } = true;
    public bool ShowClaudeOnTaskbar { get; private set; } = true;
    public bool ShowAntigravityOnTaskbar { get; private set; } = true;
    public bool ShowCursorOnTaskbar { get; private set; } = true;
    public double TaskbarFontSize { get; private set; } = 13;
    public double TaskbarIconSize { get; private set; } = 14;
    public string TaskbarFont { get; private set; } = "Chakra Petch";
    public string TaskbarTextWeight { get; private set; } = "Regular";
    public double TaskbarTextVerticalOffset { get; private set; }
    public double TaskbarRightOffset { get; private set; } = DefaultSecondaryRightOffset;
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

    public TaskbarWidgetWindow(
        TaskbarWidgetViewModel viewModel,
        TaskbarWidgetSettingsStore settingsStore,
        TaskbarWidgetPositioningService positioningService,
        TaskbarMonitorService monitorService,
        MainViewModel mainViewModel,
        IApplicationController applicationController,
        ISystemIdleTimeProvider idleTimeProvider)
        : this(viewModel, settingsStore, positioningService, monitorService, mainViewModel, applicationController, idleTimeProvider, null, null)
    {
    }

    private TaskbarWidgetWindow(
        TaskbarWidgetViewModel viewModel,
        TaskbarWidgetSettingsStore settingsStore,
        TaskbarWidgetPositioningService positioningService,
        TaskbarMonitorService monitorService,
        MainViewModel mainViewModel,
        IApplicationController applicationController,
        ISystemIdleTimeProvider idleTimeProvider,
        string? monitorId,
        TaskbarWidgetWindow? owner)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settingsStore = settingsStore;
        _positioningService = positioningService;
        _monitorService = monitorService;
        _monitorId = monitorId;
        _owner = owner;
        _mainViewModel = mainViewModel;
        _applicationController = applicationController;
        _idleTimeProvider = idleTimeProvider;
        ProvidersItemsControl.ItemsSource = _viewModel.Providers;

        var settings = _settingsStore.Load();
        ShowTaskbarWidget = settings.ShowTaskbarWidget;
        ShowCodexOnTaskbar = settings.ShowCodexOnTaskbar;
        ShowClaudeOnTaskbar = settings.ShowClaudeOnTaskbar;
        ShowAntigravityOnTaskbar = settings.ShowAntigravityOnTaskbar;
        ShowCursorOnTaskbar = settings.ShowCursorOnTaskbar;
        TaskbarFontSize = settings.TaskbarFontSize;
        TaskbarIconSize = NormalizeTaskbarIconSize(settings.TaskbarIconSize);
        TaskbarFont = NormalizeTaskbarFont(settings.TaskbarFont);
        TaskbarTextWeight = NormalizeTaskbarTextWeight(settings.TaskbarTextWeight);
        TaskbarTextVerticalOffset = NormalizeTaskbarTextVerticalOffset(settings.TaskbarTextVerticalOffset);
        GreenColorHex = settings.GreenColorHex;
        LimeColorHex = settings.LimeColorHex;
        YellowColorHex = settings.YellowColorHex;
        OrangeColorHex = settings.OrangeColorHex;
        RedColorHex = settings.RedColorHex;
        Stage1MaxPercent = settings.Stage1MaxPercent;
        Stage2MaxPercent = settings.Stage2MaxPercent;
        Stage3MaxPercent = settings.Stage3MaxPercent;
        Stage4MaxPercent = settings.Stage4MaxPercent;
        Stage5MaxPercent = settings.Stage5MaxPercent;
        if (owner is null)
        {
            var monitors = _monitorService.GetMonitors();
            foreach (var id in settings.HasTaskbarMonitorSelection
                         ? settings.EnabledTaskbarMonitorIds
                         : monitors.Where(monitor => monitor.HasTrayIcons).Select(monitor => monitor.Id))
            {
                _enabledMonitorIds.Add(id);
            }

            SyncTaskbarMonitorAppearance = settings.SyncTaskbarMonitorAppearance;
            foreach (var appearance in settings.TaskbarMonitorAppearances)
            {
                if (!string.IsNullOrWhiteSpace(appearance.MonitorId))
                {
                    _monitorAppearances[appearance.MonitorId] = appearance;
                }
            }
        }
        ApplyProviderVisibility();
        ApplySavedUsageColors();
        ApplyFontPresentation();

        _foregroundChangedHandler = OnForegroundWindowChanged;
        _taskbarLocationChangedHandler = OnTaskbarLocationChanged;
        _watchdogTimer = new DispatcherTimer { Interval = WatchdogInterval };
        _watchdogTimer.Tick += (_, _) => RunWatchdog();
        _settleTimer = new DispatcherTimer { Interval = SettleInterval };
        _settleTimer.Tick += (_, _) =>
        {
            ReassertTopMost();
            if (--_settleTicksRemaining <= 0)
            {
                _settleTimer.Stop();
            }
        };

        SourceInitialized += TaskbarWidgetWindow_SourceInitialized;
        SizeChanged += (_, _) => Reposition();
    }

    public void SetShowTaskbarWidget(bool isVisible)
    {
        if (_owner is not null)
        {
            _owner.SetShowTaskbarWidget(isVisible);
            return;
        }
        if (ShowTaskbarWidget == isVisible)
        {
            return;
        }

        ShowTaskbarWidget = isVisible;
        ApplyWindowVisibility();
        SaveSettings();
    }

    public void SetProviderVisibility(ProviderKind provider, bool isVisible)
    {
        switch (provider)
        {
            case ProviderKind.Codex:
                ShowCodexOnTaskbar = isVisible;
                break;
            case ProviderKind.Claude:
                ShowClaudeOnTaskbar = isVisible;
                break;
            case ProviderKind.Antigravity:
                ShowAntigravityOnTaskbar = isVisible;
                break;
            default:
                ShowCursorOnTaskbar = isVisible;
                break;
        }

        _viewModel.SetProviderVisible(provider, isVisible);
        SaveSettings();
    }

    public void SetTaskbarFontSize(double fontSize)
    {
        if (!double.IsFinite(fontSize))
        {
            return;
        }

        // Floor only, no ceiling - matches every other live-bound numeric field in Settings
        // (MetricLabelWidth, ProgressBarHeight, DashboardWidgetHeight). A ceiling near typical
        // values (e.g. 36) would clamp mid-typing keystrokes back to itself and make it
        // impossible to type past it, and a floor above single digits (e.g. 6) does the same
        // thing typing "1" of "14" - snaps to 6 before the second digit is entered.
        var normalized = Math.Max(1, fontSize);
        if (Math.Abs(TaskbarFontSize - normalized) < 0.01)
        {
            return;
        }

        TaskbarFontSize = normalized;
        ApplyFontPresentation();
        SaveSettings();
    }

    public IReadOnlyList<TaskbarMonitorOption> GetMonitorOptions() =>
        _monitorService.GetMonitors()
            .Select(monitor =>
            {
                var appearance = GetMonitorAppearance(monitor.Id);
                return new TaskbarMonitorOption(
                    monitor.Id,
                    monitor.DisplayName,
                    _enabledMonitorIds.Contains(monitor.Id),
                    appearance.TextSize,
                    appearance.IconSize,
                    appearance.TextVerticalOffset,
                    appearance.RightOffset ?? DefaultSecondaryRightOffset,
                    !monitor.HasTrayIcons,
                    SetMonitorEnabled,
                    SetMonitorAppearance);
            })
            .ToArray();

    public void SetSyncTaskbarMonitorAppearance(bool isEnabled)
    {
        if (SyncTaskbarMonitorAppearance == isEnabled)
        {
            return;
        }

        SyncTaskbarMonitorAppearance = isEnabled;
        SaveSettings();
    }

    private void SetMonitorEnabled(string monitorId, bool isEnabled)
    {
        if (isEnabled)
        {
            _enabledMonitorIds.Add(monitorId);
        }
        else
        {
            _enabledMonitorIds.Remove(monitorId);
        }

        SynchronizeSecondaryWindows();
        ApplyWindowVisibility();
        SaveSettings();
    }

    private TaskbarMonitorAppearanceSettings GetMonitorAppearance(string monitorId) =>
        _monitorAppearances.TryGetValue(monitorId, out var appearance)
            ? appearance
            : new TaskbarMonitorAppearanceSettings
            {
                MonitorId = monitorId,
                TextSize = TaskbarFontSize,
                IconSize = TaskbarIconSize,
                TextVerticalOffset = TaskbarTextVerticalOffset,
                RightOffset = DefaultSecondaryRightOffset
            };

    private void SetMonitorAppearance(string monitorId, double textSize, double iconSize, double verticalOffset, double rightOffset)
    {
        var normalizedTextSize = double.IsFinite(textSize) ? Math.Max(1, Math.Round(textSize)) : 1;
        var normalizedIconSize = double.IsFinite(iconSize) ? Math.Max(1, Math.Round(iconSize)) : 1;
        var normalizedOffset = double.IsFinite(verticalOffset) ? verticalOffset : 0;
        var normalizedRightOffset = double.IsFinite(rightOffset) ? Math.Max(0, rightOffset) : DefaultSecondaryRightOffset;
        var sourceAppearance = new TaskbarMonitorAppearanceSettings
        {
            MonitorId = monitorId,
            TextSize = normalizedTextSize,
            IconSize = normalizedIconSize,
            TextVerticalOffset = normalizedOffset,
            RightOffset = normalizedRightOffset
        };
        _monitorAppearances[monitorId] = sourceAppearance;
        if (SyncTaskbarMonitorAppearance)
        {
            foreach (var id in _enabledMonitorIds.Where(id => !string.Equals(id, monitorId, StringComparison.OrdinalIgnoreCase)))
            {
                var current = GetMonitorAppearance(id);
                _monitorAppearances[id] = new TaskbarMonitorAppearanceSettings
                {
                    MonitorId = id,
                    TextSize = normalizedTextSize,
                    IconSize = normalizedIconSize,
                    TextVerticalOffset = normalizedOffset,
                    RightOffset = current.RightOffset ?? DefaultSecondaryRightOffset
                };
            }
        }

        ApplyMonitorAppearances();
        SaveSettings();
    }

    private void ApplyMonitorAppearances()
    {
        ApplyMonitorAppearance(this);
        foreach (var window in _secondaryWindows.Values)
        {
            ApplyMonitorAppearance(window);
        }
    }

    private void ApplyMonitorAppearance(TaskbarWidgetWindow window)
    {
        var monitor = window.GetTargetMonitor();
        if (monitor is null)
        {
            return;
        }

        var appearance = GetMonitorAppearance(monitor.Id);
        window.TaskbarFontSize = appearance.TextSize;
        window.TaskbarIconSize = appearance.IconSize;
        window.TaskbarTextVerticalOffset = appearance.TextVerticalOffset;
        window.TaskbarRightOffset = appearance.RightOffset ?? DefaultSecondaryRightOffset;
        window.ApplyFontPresentation();
    }

    public void SetTaskbarIconSize(double iconSize)
    {
        if (!double.IsFinite(iconSize))
        {
            return;
        }

        var normalized = Math.Max(1, iconSize);
        if (Math.Abs(TaskbarIconSize - normalized) < 0.01)
        {
            return;
        }

        TaskbarIconSize = normalized;
        ApplyFontPresentation();
        SaveSettings();
    }

    public void SetTaskbarFont(string font)
    {
        var normalized = NormalizeTaskbarFont(font);
        if (TaskbarFont == normalized)
        {
            return;
        }

        TaskbarFont = normalized;
        ApplyFontPresentation();
        SaveSettings();
    }

    public void SetTaskbarTextWeight(string weight)
    {
        var normalized = NormalizeTaskbarTextWeight(weight);
        if (TaskbarTextWeight == normalized)
        {
            return;
        }

        TaskbarTextWeight = normalized;
        ApplyFontPresentation();
        SaveSettings();
    }

    public void SetTaskbarTextVerticalOffset(double verticalOffset)
    {
        if (!double.IsFinite(verticalOffset))
        {
            return;
        }

        if (Math.Abs(TaskbarTextVerticalOffset - verticalOffset) < 0.01)
        {
            return;
        }

        TaskbarTextVerticalOffset = verticalOffset;
        ApplyFontPresentation();
        SaveSettings();
    }

    private void ApplyFontPresentation()
    {
        Resources["TaskbarMetricFontSize"] = TaskbarFontSize;
        Resources["TaskbarProviderIconSize"] = TaskbarIconSize;
        Resources["TaskbarMetricFontFamily"] = CreateTaskbarFontFamily(TaskbarFont);
        Resources["TaskbarMetricFontWeight"] = TaskbarTextWeight switch
        {
            "Bold" => FontWeights.Bold,
            "SemiBold" => FontWeights.SemiBold,
            _ => FontWeights.Normal
        };
        Resources["TaskbarMetricVerticalOffset"] = TaskbarTextVerticalOffset;
    }

    private static System.Windows.Media.FontFamily CreateTaskbarFontFamily(string font)
    {
        if (font == "Segoe UI Variable Text")
        {
            return new System.Windows.Media.FontFamily(font);
        }

        return new System.Windows.Media.FontFamily(
            new Uri("pack://application:,,,/"),
            $"./Assets/fonts/#{font}");
    }

    private static string NormalizeTaskbarFont(string? font) => font switch
    {
        "Segoe UI Variable Text" or "VT323" or "Pixelify Sans" or "Silkscreen" or "Tiny5" or
        "Space Mono" or "Chakra Petch" or "IBM Plex Mono" or "DotGothic16" or "Handjet" or
        "Rajdhani" or "Oxanium" or "Kode Mono" => font,
        _ => "Segoe UI Variable Text"
    };

    private static string NormalizeTaskbarTextWeight(string? weight) => weight switch
    {
        "Regular" or "Bold" => weight,
        _ => "SemiBold"
    };

    private static double NormalizeTaskbarIconSize(double iconSize) =>
        double.IsFinite(iconSize) && iconSize >= 1 ? iconSize : 14;

    private static double NormalizeTaskbarTextVerticalOffset(double verticalOffset) =>
        double.IsFinite(verticalOffset) ? verticalOffset : 0;

    // The taskbar has its own converter instance, but the app applies the main window's shared
    // usage-colour-stage settings to it whenever settings are loaded or changed.
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
        // Both widgets bind to the same shared UsageMetricViewModel instances (see
        // TaskbarWidgetViewModel), each through its own converter - forcing a PropertyChanged
        // here is what makes the already-visible values re-run through this widget's converter
        // with its newly configured colours, without waiting for the next real usage update.
        _mainViewModel.RefreshUsageColors();
        SaveSettings();
        return true;
    }

    public void ResetUsageColorsToDefault() =>
        TrySetUsageColors("#2ECC71", "#9ACD32", "#FFD21E", "#FF9800", "#FF4D4F", 29, 49, 69, 79, 84);

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

    private void ApplyProviderVisibility()
    {
        _viewModel.SetProviderVisible(ProviderKind.Codex, ShowCodexOnTaskbar);
        _viewModel.SetProviderVisible(ProviderKind.Claude, ShowClaudeOnTaskbar);
        _viewModel.SetProviderVisible(ProviderKind.Antigravity, ShowAntigravityOnTaskbar);
        _viewModel.SetProviderVisible(ProviderKind.Cursor, ShowCursorOnTaskbar);
    }

    public void ApplyStartupVisibility()
    {
        SynchronizeSecondaryWindows();
        ApplyWindowVisibility();
    }

    private void ApplyWindowVisibility()
    {
        var monitor = GetTargetMonitor();
        var isEnabledForMonitor = _owner is null
            ? monitor is not null && _enabledMonitorIds.Contains(monitor.Id)
            : _owner.ShowTaskbarWidget && _owner._enabledMonitorIds.Contains(_monitorId!);
        var isEnabled = ShowTaskbarWidget && isEnabledForMonitor;
        // Recomputed here rather than trusted from the last event, so the very first call (app
        // start) already knows about a game that was fullscreen before this process existed and
        // never paints a frame of the widget on top of it.
        _isFullscreenActive = ComputeFullscreenActive(monitor);
        if (isEnabled && !_isFullscreenActive)
        {
            if (IsLoaded)
            {
                // A hidden WPF window retains its last coordinates. Move it while it is still
                // hidden so re-enabling a monitor never paints one frame at the stale location.
                Reposition();
                Show();
            }
            else
            {
                // The first Show is required before ActualWidth and the per-monitor DPI transform
                // exist. Keep that initial layout invisible, position it, then reveal it.
                Opacity = 0;
                Show();
                UpdateLayout();
                Reposition();
                Opacity = 1;
            }

            _watchdogTimer.Start();
        }
        else
        {
            _settleTimer.Stop();
            Hide();

            // Hidden for fullscreen is not the same as switched off: the watchdog keeps running
            // so there is still a backstop that notices fullscreen ending, in case the WinEvent
            // hooks miss it (they are the fast path, not a guarantee).
            if (isEnabled)
            {
                // Those hooks are installed from SourceInitialized, which only runs on the first
                // Show - so a widget that has been hidden for fullscreen since app start would
                // never have any, leaving the 10s watchdog as its only way back. Materializing
                // the handle (without showing the window) installs them now instead.
                new WindowInteropHelper(this).EnsureHandle();
                _watchdogTimer.Start();
            }
            else
            {
                _watchdogTimer.Stop();
            }
        }
    }

    private bool ComputeFullscreenActive(TaskbarMonitor? monitor)
    {
        // The taskbar handle is the anchor because it is by definition on this widget's monitor,
        // which our own window is not yet guaranteed to be before its first Reposition. The
        // uncached lookup is the path taken before SourceInitialized has run - answering "not
        // fullscreen" there instead would flip the state back and forth on every check.
        var anchor = _cachedTaskbarHandle;
        if (anchor == IntPtr.Zero)
        {
            anchor = (monitor ?? GetTargetMonitor())?.TaskbarHandle ?? IntPtr.Zero;
        }

        return TaskbarInterop.IsMonitorCoveredByFullscreenWindow(anchor);
    }

    // Cheap enough to run on every foreground/location event: it only touches the window tree,
    // and only escalates to the (comparatively expensive) visibility pass on an actual transition.
    private void RefreshFullscreenState()
    {
        _fullscreenCheckQueued = false;
        if (!ShowTaskbarWidget || ComputeFullscreenActive(null) == _isFullscreenActive)
        {
            return;
        }

        ApplyWindowVisibility();
        if (!_isFullscreenActive && IsVisible)
        {
            // Coming back from fullscreen: the app that just exited will have reshuffled the
            // topmost band on its way out, and Explorer re-asserts the taskbar as it redraws, so
            // one Show is not enough to keep the slot - see BeginTopMostSettle.
            BeginTopMostSettle();
        }
    }

    private void QueueFullscreenCheck()
    {
        if (_fullscreenCheckQueued)
        {
            return;
        }

        _fullscreenCheckQueued = true;
        Dispatcher.BeginInvoke(RefreshFullscreenState);
    }

    private TaskbarMonitor? GetTargetMonitor()
    {
        var monitors = _monitorService.GetMonitors();
        return _monitorId is null
            ? monitors.FirstOrDefault(monitor => monitor.HasTrayIcons)
            : monitors.FirstOrDefault(monitor => string.Equals(monitor.Id, _monitorId, StringComparison.OrdinalIgnoreCase));
    }

    private void SynchronizeSecondaryWindows()
    {
        if (_owner is not null)
        {
            return;
        }

        var monitors = _monitorService.GetMonitors();
        var trayMonitorId = monitors.FirstOrDefault(monitor => monitor.HasTrayIcons)?.Id;
        var requiredIds = _enabledMonitorIds.Where(id => !string.Equals(id, trayMonitorId, StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in _secondaryWindows.Keys.Where(id => !requiredIds.Contains(id)).ToArray())
        {
            _secondaryWindows[id].Close();
            _secondaryWindows.Remove(id);
        }

        foreach (var monitor in monitors.Where(monitor => requiredIds.Contains(monitor.Id)))
        {
            if (!_secondaryWindows.TryGetValue(monitor.Id, out var window))
            {
                window = new TaskbarWidgetWindow(_viewModel, _settingsStore, _positioningService, _monitorService, _mainViewModel, _applicationController, _idleTimeProvider, monitor.Id, this);
                _secondaryWindows.Add(monitor.Id, window);
            }

            window.ApplyOwnerState(this);
            window.ApplyWindowVisibility();
        }
    }

    private void ApplyOwnerState(TaskbarWidgetWindow owner)
    {
        ShowTaskbarWidget = owner.ShowTaskbarWidget;
        ShowCodexOnTaskbar = owner.ShowCodexOnTaskbar;
        ShowClaudeOnTaskbar = owner.ShowClaudeOnTaskbar;
        ShowAntigravityOnTaskbar = owner.ShowAntigravityOnTaskbar;
        ShowCursorOnTaskbar = owner.ShowCursorOnTaskbar;
        var appearance = owner.GetMonitorAppearance(_monitorId!);
        TaskbarFontSize = appearance.TextSize;
        TaskbarIconSize = appearance.IconSize;
        TaskbarFont = owner.TaskbarFont;
        TaskbarTextWeight = owner.TaskbarTextWeight;
        TaskbarTextVerticalOffset = appearance.TextVerticalOffset;
        TaskbarRightOffset = appearance.RightOffset ?? DefaultSecondaryRightOffset;
        GreenColorHex = owner.GreenColorHex;
        LimeColorHex = owner.LimeColorHex;
        YellowColorHex = owner.YellowColorHex;
        OrangeColorHex = owner.OrangeColorHex;
        RedColorHex = owner.RedColorHex;
        Stage1MaxPercent = owner.Stage1MaxPercent;
        Stage2MaxPercent = owner.Stage2MaxPercent;
        Stage3MaxPercent = owner.Stage3MaxPercent;
        Stage4MaxPercent = owner.Stage4MaxPercent;
        Stage5MaxPercent = owner.Stage5MaxPercent;
        ApplyFontPresentation();
        TryConfigureUsageColors(GreenColorHex, LimeColorHex, YellowColorHex, OrangeColorHex, RedColorHex, Stage1MaxPercent, Stage2MaxPercent, Stage3MaxPercent, Stage4MaxPercent, Stage5MaxPercent);
    }

    private void TaskbarWidgetWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var currentStyle = TaskbarInterop.GetWindowExStyle(handle).ToInt64();
        var newStyle = currentStyle | TaskbarInterop.WsExToolWindow | TaskbarInterop.WsExNoActivate;
        TaskbarInterop.SetWindowExStyle(handle, new IntPtr(newStyle));

        _taskbarCreatedMessage = TaskbarInterop.RegisterWindowMessage("TaskbarCreated");
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowMessageHook);

        RefreshCachedTaskbarHandles();

        // EVENT_SYSTEM_FOREGROUND: fires the instant any window anywhere becomes the foreground
        // window - exactly what happens when a taskbar button is clicked. This is what keeps
        // HWND_TOPMOST from being lost to Explorer re-asserting its own topmost position, without
        // needing to poll for it.
        _foregroundHook = TaskbarInterop.SetWinEventHook(
            TaskbarInterop.EventSystemForeground,
            TaskbarInterop.EventSystemForeground,
            IntPtr.Zero,
            _foregroundChangedHandler,
            0,
            0,
            TaskbarInterop.WinEventOutOfContext);

        // EVENT_OBJECT_LOCATIONCHANGE fires for every window move/resize system-wide, so the
        // callback must filter down to just the taskbar itself before doing anything.
        //
        // The breadth is deliberate but not free, and the numbers are worth recording because
        // they are not obvious from the API: measured on a 4-monitor setup, this hook receives
        // ~126 events/sec, of which 97.8% are OBJID_CURSOR (plain mouse movement) and 1.7% are
        // OBJID_CARET - all discarded on the callback's first line. Only ~0.5% are real window
        // moves. Across four widget windows that is ~504 deliveries/sec into this UI thread, or
        // roughly 0.05% of a core. Cheap enough that the two available optimisations were
        // considered and deliberately skipped:
        //
        //   * Narrowing the scope. idProcess/idThread are passed as 0/0 here, so Windows filters
        //     nothing. Scoping this hook to Explorer's thread would drop the other processes'
        //     cursor traffic at the OS level, since the only handles the callback acts on
        //     (Shell_TrayWnd, TrayNotifyWnd) are Explorer's. What blocks it is the fullscreen
        //     check: that branch compares against the FOREGROUND window, which can belong to any
        //     process. Doing it properly needs a second hook re-scoped to the foreground window's
        //     thread on every focus change - more state than 0.05% of a core justifies.
        //
        //   * Sharing one hook across the windows instead of one per window (3 of every 4
        //     deliveries exist only to bail out). Rejected on correctness, not cost: hooks are
        //     installed from SourceInitialized, which only runs once a window is actually shown,
        //     and the owner window is only shown when the tray-icon monitor is enabled. A shared
        //     hook owned by it would leave every secondary widget deaf whenever the user turns
        //     that one monitor off. Per-window hooks have no such lifetime coupling.
        _locationHook = TaskbarInterop.SetWinEventHook(
            TaskbarInterop.EventObjectLocationChange,
            TaskbarInterop.EventObjectLocationChange,
            IntPtr.Zero,
            _taskbarLocationChangedHandler,
            0,
            0,
            TaskbarInterop.WinEventOutOfContext | TaskbarInterop.WinEventSkipOwnProcess);
    }

    private void OnForegroundWindowChanged(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        // Kept minimal per the hook contract: just re-claim the topmost slot, no repositioning.
        // The fullscreen re-check rides along because a game or a video taking focus IS a
        // foreground change - and BeginTopMostSettle stands down once that check says so.
        Dispatcher.BeginInvoke(HandleForegroundChanged);
    }

    private void HandleForegroundChanged()
    {
        RefreshFullscreenState();
        BeginTopMostSettle();
    }

    // Only re-asserts when something is actually covering us. The check matters as much as the
    // fix: this window is layered (AllowsTransparency), so an unconditional SetWindowPos costs a
    // repaint that is itself visible as a flicker - and with the settle sequence below firing
    // several times per event, blind re-asserting turns one brief flicker into several.
    private void ReassertTopMost()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (!ShowTaskbarWidget || _isFullscreenActive || handle == IntPtr.Zero)
        {
            return;
        }

        if (!TaskbarInterop.GetWindowRect(handle, out var rect))
        {
            TaskbarInterop.ForceTopMost(handle);
            return;
        }

        var centerX = (rect.Left + rect.Right) / 2;
        var centerY = (rect.Top + rect.Bottom) / 2;
        if (TaskbarInterop.IsObscuredAt(handle, centerX, centerY))
        {
            TaskbarInterop.ForceTopMost(handle);
        }
    }

    // Re-assert now, then again a few times over Explorer's settling window - see SettleInterval.
    private void BeginTopMostSettle()
    {
        if (!ShowTaskbarWidget || _isFullscreenActive)
        {
            return;
        }

        ReassertTopMost();
        _settleTicksRemaining = SettleTickCount;
        _settleTimer.Stop();
        _settleTimer.Start();
    }

    private void OnTaskbarLocationChanged(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        // Reorganizing which tray icons are hidden/shown resizes TrayNotifyWnd without Shell_
        // TrayWnd's own outer bounds changing at all, so both handles need watching - only
        // Shell_TrayWnd moving/resizing wouldn't catch that case.
        if (idObject != TaskbarInterop.ObjIdWindow)
        {
            return;
        }

        if (hwnd == _cachedTaskbarHandle || hwnd == _cachedTrayNotifyHandle)
        {
            Dispatcher.BeginInvoke(Reposition);
            return;
        }

        // Toggling fullscreen inside the window that already has focus (F11, a video player's
        // fullscreen button) resizes it without any foreground change, so the hook above never
        // fires for it - this is the only signal that case produces.
        if (hwnd == TaskbarInterop.GetForegroundWindow())
        {
            QueueFullscreenCheck();
        }
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == TaskbarInterop.WmWindowPosChanging)
        {
            InterceptWindowPosChanging(lParam);
            return IntPtr.Zero;
        }

        if (message == TaskbarInterop.WmDisplayChange || (uint)message == _taskbarCreatedMessage)
        {
            // Explorer restarting or a display change invalidates whichever handles were cached
            // for the location-change filter above.
            RefreshCachedTaskbarHandles();
            SynchronizeSecondaryWindows();
            // Which monitor this widget lives on may have just changed, and that monitor is what
            // the fullscreen check is asked about.
            QueueFullscreenCheck();
            Dispatcher.BeginInvoke(Reposition, DispatcherPriority.Background);
        }

        return IntPtr.Zero;
    }

    // Rewrites a pending z-order change to this window in place, before Windows applies it.
    // NOTE: this only covers changes to OUR OWN window - it does nothing for the common case of
    // Explorer raising the taskbar above us, which moves the taskbar rather than us and so sends
    // no message here at all. That case is handled by BeginTopMostSettle/RunWatchdog instead.
    private void InterceptWindowPosChanging(IntPtr lParam)
    {
        if (!ShowTaskbarWidget || _isFullscreenActive)
        {
            return;
        }

        var pos = Marshal.PtrToStructure<TaskbarInterop.WindowPos>(lParam);
        if (pos.HwndInsertAfter == TaskbarInterop.HwndTopmost &&
            (pos.Flags & TaskbarInterop.SwpNoZOrder) == 0)
        {
            return;
        }

        pos.HwndInsertAfter = TaskbarInterop.HwndTopmost;
        pos.Flags &= ~TaskbarInterop.SwpNoZOrder;
        Marshal.StructureToPtr(pos, lParam, false);
    }

    private void RefreshCachedTaskbarHandles()
    {
        _cachedTaskbarHandle = GetTargetMonitor()?.TaskbarHandle ?? IntPtr.Zero;
        _cachedTrayNotifyHandle = TaskbarInterop.FindTrayNotifyArea(_cachedTaskbarHandle);
    }

    private void Reposition()
    {
        if (!ShowTaskbarWidget || _isFullscreenActive || !IsLoaded)
        {
            return;
        }

        var monitor = GetTargetMonitor();
        if (monitor is not null && _positioningService.TryComputePosition(
                this,
                ActualWidth,
                monitor.TaskbarHandle,
                monitor.HasTrayIcons,
                TaskbarRightOffset,
                out var left,
                out var top,
                out var taskbarHeight))
        {
            Left = left;
            Top = top;
            Height = taskbarHeight;
        }

        ReassertTopMost();
    }

    // Recovery backstop - see WatchdogInterval for why the topmost re-assert here is
    // unconditional rather than gated on the position/size checks below.
    private void RunWatchdog()
    {
        if (!ShowTaskbarWidget)
        {
            return;
        }

        if (_idleTimeProvider.GetIdleTime() >= IdleThreshold)
        {
            // No input at all, so nothing user-driven can be reordering windows - see
            // IdleThreshold. One cheap read and we're done until input resumes.
            _wasIdle = true;
            return;
        }

        if (_wasIdle)
        {
            _wasIdle = false;
            // Returning from idle is exactly when taskbar geometry and z-order are most likely to
            // have been shuffled while we deliberately weren't looking (lock screen, monitor
            // sleep/wake, resolution or DPI change on resume), so do the full correction rather
            // than a single check.
            RefreshCachedTaskbarHandles();
            RefreshFullscreenState();
            if (_isFullscreenActive)
            {
                return;
            }

            Reposition();
            BeginTopMostSettle();
            return;
        }

        // Backstop for the WinEvent hooks - and the reason IsLoaded is checked here rather than
        // at the top of the method: while a fullscreen app has kept the widget hidden since
        // startup it was never loaded, and bailing on that would strand it hidden forever.
        RefreshFullscreenState();
        if (_isFullscreenActive || !IsLoaded)
        {
            return;
        }

        ReassertTopMost();

        var currentTaskbarHandle = GetTargetMonitor()?.TaskbarHandle ?? IntPtr.Zero;
        var currentTrayNotifyHandle = TaskbarInterop.FindTrayNotifyArea(currentTaskbarHandle);
        if (currentTaskbarHandle != _cachedTaskbarHandle || currentTrayNotifyHandle != _cachedTrayNotifyHandle)
        {
            _cachedTaskbarHandle = currentTaskbarHandle;
            _cachedTrayNotifyHandle = currentTrayNotifyHandle;
            Reposition();
            return;
        }

        if (!IsVisible)
        {
            Show();
            Reposition();
            return;
        }

        var monitor = GetTargetMonitor();
        if (monitor is not null && _positioningService.TryComputePosition(
                this,
                ActualWidth,
                monitor.TaskbarHandle,
                monitor.HasTrayIcons,
                TaskbarRightOffset,
                out var expectedLeft,
                out var expectedTop,
                out var expectedHeight) &&
            (Math.Abs(Left - expectedLeft) > PositionToleranceDip ||
             Math.Abs(Top - expectedTop) > PositionToleranceDip ||
             Math.Abs(Height - expectedHeight) > PositionToleranceDip))
        {
            Reposition();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _watchdogTimer.Stop();
        _settleTimer.Stop();
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;

        if (_foregroundHook != IntPtr.Zero)
        {
            TaskbarInterop.UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }

        if (_locationHook != IntPtr.Zero)
        {
            TaskbarInterop.UnhookWinEvent(_locationHook);
            _locationHook = IntPtr.Zero;
        }

        base.OnClosed(e);
    }

    // The context menu's own popup sits above everything (including the taskbar) while open, so
    // closing it is a known point where this window can end up a slot lower in the topmost band.
    // Worth handling explicitly rather than waiting for whatever gets clicked next to fire
    // EVENT_SYSTEM_FOREGROUND.
    private void ContextMenu_Closed(object sender, RoutedEventArgs e) => BeginTopMostSettle();

    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e) => _applicationController.ShowSettings();

    private async void RefreshAllMenuItem_Click(object sender, RoutedEventArgs e) =>
        await _applicationController.RefreshAllAsync();

    private void HideMenuItem_Click(object sender, RoutedEventArgs e) => SetShowTaskbarWidget(false);

    private void SaveSettings()
    {
        if (_owner is not null)
        {
            return;
        }

        SynchronizeSecondaryWindows();
        _settingsStore.Save(new TaskbarWidgetSettings
        {
            ShowTaskbarWidget = ShowTaskbarWidget,
            HasTaskbarMonitorSelection = true,
            EnabledTaskbarMonitorIds = _enabledMonitorIds.ToList(),
            SyncTaskbarMonitorAppearance = SyncTaskbarMonitorAppearance,
            TaskbarMonitorAppearances = _monitorAppearances.Values.ToList(),
            ShowCodexOnTaskbar = ShowCodexOnTaskbar,
            ShowClaudeOnTaskbar = ShowClaudeOnTaskbar,
            ShowAntigravityOnTaskbar = ShowAntigravityOnTaskbar,
            ShowCursorOnTaskbar = ShowCursorOnTaskbar,
            TaskbarFontSize = TaskbarFontSize,
            TaskbarIconSize = TaskbarIconSize,
            TaskbarFont = TaskbarFont,
            TaskbarTextWeight = TaskbarTextWeight,
            TaskbarTextVerticalOffset = TaskbarTextVerticalOffset,
            GreenColorHex = GreenColorHex,
            LimeColorHex = LimeColorHex,
            YellowColorHex = YellowColorHex,
            OrangeColorHex = OrangeColorHex,
            RedColorHex = RedColorHex,
            Stage1MaxPercent = Stage1MaxPercent,
            Stage2MaxPercent = Stage2MaxPercent,
            Stage3MaxPercent = Stage3MaxPercent,
            Stage4MaxPercent = Stage4MaxPercent,
            Stage5MaxPercent = Stage5MaxPercent
        });

        // Every state setter routes through here - see WidgetStateChanged.
        WidgetStateChanged?.Invoke();
    }
}
