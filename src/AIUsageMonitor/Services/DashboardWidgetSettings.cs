using System.IO;
using System.Text.Json;
using AIUsageMonitor.Converters;
using AIUsageMonitor.Models;

namespace AIUsageMonitor.Services;

/// <summary>
/// Persistent dashboard configuration. This deliberately contains no Window reference so the
/// dashboard can be closed and collected while Settings and the tray application stay alive.
/// </summary>
public sealed class DashboardWidgetSettings
{
    public const double DefaultDashboardWidgetHeight = 50;
    public const double DefaultMetricLabelWidth = 27;
    public const double DefaultProgressBarHeight = 2;

    private readonly string _settingsPath;

    public DashboardWidgetSettings()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIUsageMonitor",
            "window-placement.json"))
    {
    }

    internal DashboardWidgetSettings(string settingsPath)
    {
        _settingsPath = settingsPath;
        Load();
    }

    public event Action? Changed;

    public bool HasSavedPlacement { get; private set; }
    public double Left { get; private set; }
    public double Top { get; private set; }
    public double Width { get; private set; } = 430;
    public double Height { get; private set; } = 320;
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
    public bool HideAntigravityClaudeAndGptModels { get; private set; }
    public bool HideAntigravityFiveHourLimits { get; private set; }
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
    public double Opacity { get; private set; } = 1.0;

    public void SetWindowLocked(bool value) => Set(IsWindowLocked, value, updated => IsWindowLocked = updated);
    public void SetDashboardLayoutEnabled(bool value) =>
        Set(IsDashboardLayoutEnabled, value, updated => IsDashboardLayoutEnabled = updated);
    public void SetHorizontalLayout(bool value) => Set(IsHorizontalLayout, value, updated => IsHorizontalLayout = updated);
    public void SetDashboardWidgetVisible(bool value) =>
        Set(ShowDashboardWidget, value, updated => ShowDashboardWidget = updated);
    public void SetAlwaysOnTop(bool value) => Set(AlwaysOnTop, value, updated => AlwaysOnTop = updated);
    public void SetAutoRefreshEnabled(bool value) => Set(AutoRefreshEnabled, value, updated => AutoRefreshEnabled = updated);
    public void SetShowUsageRemaining(bool value) =>
        Set(ShowUsageRemaining, value, updated => ShowUsageRemaining = updated);
    public void SetHideAntigravityClaudeAndGptModels(bool value) =>
        Set(HideAntigravityClaudeAndGptModels, value, updated => HideAntigravityClaudeAndGptModels = updated);
    public void SetHideAntigravityFiveHourLimits(bool value) =>
        Set(HideAntigravityFiveHourLimits, value, updated => HideAntigravityFiveHourLimits = updated);

    public void SetDashboardWidgetHeight(double value)
    {
        if (double.IsFinite(value))
        {
            Set(DashboardWidgetHeight, Math.Max(1, Math.Round(value)), updated => DashboardWidgetHeight = updated);
        }
    }

    public void SetMetricLabelWidth(double value)
    {
        if (double.IsFinite(value))
        {
            Set(MetricLabelWidth, Math.Max(1, Math.Round(value)), updated => MetricLabelWidth = updated);
        }
    }

    public void SetProgressBarHeight(double value)
    {
        if (double.IsFinite(value))
        {
            Set(ProgressBarHeight, Math.Max(1, Math.Round(value)), updated => ProgressBarHeight = updated);
        }
    }

    public void SetProviderVisibility(ProviderKind provider, bool isVisible)
    {
        switch (provider)
        {
            case ProviderKind.Codex:
                Set(ShowCodex, isVisible, updated => ShowCodex = updated);
                break;
            case ProviderKind.Claude:
                Set(ShowClaude, isVisible, updated => ShowClaude = updated);
                break;
            case ProviderKind.Antigravity:
                Set(ShowAntigravity, isVisible, updated => ShowAntigravity = updated);
                break;
            default:
                Set(ShowCursor, isVisible, updated => ShowCursor = updated);
                break;
        }
    }

    public void SetFontSizePreset(string value) =>
        Set(FontSizePreset, NormalizeFontSizePreset(value), updated => FontSizePreset = updated);
    public void SetWidgetFont(string value) => Set(WidgetFont, NormalizeWidgetFont(value), updated => WidgetFont = updated);
    public void SetWidgetAppearance(string value) =>
        Set(WidgetAppearance, NormalizeWidgetAppearance(value), updated => WidgetAppearance = updated);
    public void SetWidgetTextWeight(string value) =>
        Set(WidgetTextWeight, NormalizeWidgetTextWeight(value), updated => WidgetTextWeight = updated);
    public void SetOpacity(double value)
    {
        if (double.IsFinite(value))
        {
            Set(Opacity, Math.Clamp(value, 0.6, 1.0), updated => Opacity = updated);
        }
    }

    public void SetRefreshInterval(ProviderKind provider, double minutes)
    {
        var normalized = AutoRefreshOptions.NormalizeInterval(minutes);
        switch (provider)
        {
            case ProviderKind.Codex:
                Set(CodexRefreshIntervalMinutes, normalized, updated => CodexRefreshIntervalMinutes = updated);
                break;
            case ProviderKind.Claude:
                Set(ClaudeRefreshIntervalMinutes, normalized, updated => ClaudeRefreshIntervalMinutes = updated);
                break;
            case ProviderKind.Antigravity:
                Set(AntigravityRefreshIntervalMinutes, normalized, updated => AntigravityRefreshIntervalMinutes = updated);
                break;
            default:
                Set(CursorRefreshIntervalMinutes, normalized, updated => CursorRefreshIntervalMinutes = updated);
                break;
        }
    }

    public void SetIdleRefreshOptions(double idleAfterMinutes, double idleRefreshIntervalMinutes)
    {
        var idleAfter = AutoRefreshOptions.NormalizeInterval(idleAfterMinutes);
        var idleRefresh = AutoRefreshOptions.NormalizeInterval(idleRefreshIntervalMinutes);
        if (IdleAfterMinutes.Equals(idleAfter) && IdleRefreshIntervalMinutes.Equals(idleRefresh))
        {
            return;
        }

        IdleAfterMinutes = idleAfter;
        IdleRefreshIntervalMinutes = idleRefresh;
        SaveAndNotify();
    }

    public void SetThrottleInterval(ProviderKind provider, double minutes)
    {
        var normalized = AutoRefreshOptions.NormalizeThrottle(minutes);
        switch (provider)
        {
            case ProviderKind.Codex:
                Set(CodexThrottleIntervalMinutes, normalized, updated => CodexThrottleIntervalMinutes = updated);
                break;
            case ProviderKind.Claude:
                Set(ClaudeThrottleIntervalMinutes, normalized, updated => ClaudeThrottleIntervalMinutes = updated);
                break;
            case ProviderKind.Antigravity:
                Set(AntigravityThrottleIntervalMinutes, normalized, updated => AntigravityThrottleIntervalMinutes = updated);
                break;
            default:
                Set(CursorThrottleIntervalMinutes, normalized, updated => CursorThrottleIntervalMinutes = updated);
                break;
        }
    }

    public void ResetScheduledIntervalsToDefault()
    {
        CodexRefreshIntervalMinutes = AutoRefreshOptions.CodexDefaultIntervalMinutes;
        ClaudeRefreshIntervalMinutes = AutoRefreshOptions.ClaudeDefaultIntervalMinutes;
        AntigravityRefreshIntervalMinutes = AutoRefreshOptions.AntigravityDefaultIntervalMinutes;
        CursorRefreshIntervalMinutes = AutoRefreshOptions.CursorDefaultIntervalMinutes;
        IdleAfterMinutes = AutoRefreshOptions.DefaultIdleAfterMinutes;
        IdleRefreshIntervalMinutes = AutoRefreshOptions.DefaultIdleRefreshIntervalMinutes;
        SaveAndNotify();
    }

    public void ResetThrottleIntervalsToDefault()
    {
        CodexThrottleIntervalMinutes = AutoRefreshOptions.CodexDefaultThrottleMinutes;
        ClaudeThrottleIntervalMinutes = AutoRefreshOptions.ClaudeDefaultThrottleMinutes;
        AntigravityThrottleIntervalMinutes = AutoRefreshOptions.AntigravityDefaultThrottleMinutes;
        CursorThrottleIntervalMinutes = AutoRefreshOptions.CursorDefaultThrottleMinutes;
        SaveAndNotify();
    }

    public void ResetUsageColorsToDefault() =>
        TrySetUsageColors("#2ECC71", "#9ACD32", "#FFD21E", "#FF9800", "#FF4D4F", 29, 49, 69, 79, 84);

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
        if (!AreValidUsageColors(
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
        SaveAndNotify();
        return true;
    }

    public void UpdateWindowPlacement(double left, double top, double width, double height)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top) ||
            !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
        {
            return;
        }

        Left = left;
        Top = top;
        Width = width;
        Height = height;
        HasSavedPlacement = true;
        Save();
    }

    private void Load()
    {
        var placement = Read();
        if (placement is null)
        {
            return;
        }

        HasSavedPlacement = placement.HasSavedPlacement ??
                            (double.IsFinite(placement.Left) && double.IsFinite(placement.Top));
        Left = placement.Left;
        Top = placement.Top;
        Width = double.IsFinite(placement.Width) && placement.Width > 0 ? placement.Width : Width;
        Height = double.IsFinite(placement.Height) && placement.Height > 0 ? placement.Height : Height;
        IsWindowLocked = placement.IsLocked;
        IsDashboardLayoutEnabled = placement.IsDashboardLayoutEnabled;
        DashboardWidgetHeight = NormalizePositive(placement.DashboardWidgetHeight, DefaultDashboardWidgetHeight);
        MetricLabelWidth = NormalizePositive(placement.MetricLabelWidth, DefaultMetricLabelWidth);
        ProgressBarHeight = NormalizePositive(placement.ProgressBarHeight, DefaultProgressBarHeight);
        IsHorizontalLayout = placement.IsHorizontalLayout;
        ShowDashboardWidget = placement.ShowDashboardWidget;
        AlwaysOnTop = placement.AlwaysOnTop;
        ShowCodex = placement.ShowCodex;
        ShowClaude = placement.ShowClaude;
        ShowAntigravity = placement.ShowAntigravity;
        HideAntigravityClaudeAndGptModels = placement.HideAntigravityClaudeAndGptModels;
        HideAntigravityFiveHourLimits = placement.HideAntigravityFiveHourLimits;
        ShowCursor = placement.ShowCursor;
        FontSizePreset = NormalizeFontSizePreset(placement.FontSizePreset);
        WidgetFont = NormalizeWidgetFont(placement.WidgetFont ?? ExtractWidgetFont(placement.WidgetStyle));
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
        CodexRefreshIntervalMinutes = AutoRefreshOptions.NormalizeInterval(placement.CodexRefreshIntervalMinutes);
        ClaudeRefreshIntervalMinutes = AutoRefreshOptions.NormalizeInterval(placement.ClaudeRefreshIntervalMinutes);
        AntigravityRefreshIntervalMinutes = AutoRefreshOptions.NormalizeInterval(placement.AntigravityRefreshIntervalMinutes);
        CursorRefreshIntervalMinutes = AutoRefreshOptions.NormalizeInterval(placement.CursorRefreshIntervalMinutes);
        IdleAfterMinutes = AutoRefreshOptions.NormalizeInterval(placement.IdleAfterMinutes);
        IdleRefreshIntervalMinutes = AutoRefreshOptions.NormalizeInterval(placement.IdleRefreshIntervalMinutes);
        CodexThrottleIntervalMinutes = AutoRefreshOptions.NormalizeThrottle(placement.CodexThrottleIntervalMinutes);
        ClaudeThrottleIntervalMinutes = AutoRefreshOptions.NormalizeThrottle(placement.ClaudeThrottleIntervalMinutes);
        AntigravityThrottleIntervalMinutes = AutoRefreshOptions.NormalizeThrottle(placement.AntigravityThrottleIntervalMinutes);
        CursorThrottleIntervalMinutes = AutoRefreshOptions.NormalizeThrottle(placement.CursorThrottleIntervalMinutes);
        Opacity = Math.Clamp(placement.Opacity, 0.6, 1.0);

        if (!AreValidUsageColors(
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
        }
    }

    private WindowPlacement? Read()
    {
        try
        {
            return File.Exists(_settingsPath)
                ? JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(_settingsPath))
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

    private void SaveAndNotify()
    {
        Save();
        Changed?.Invoke();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var temporaryPath = _settingsPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(CreatePlacement()));
            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private WindowPlacement CreatePlacement() => new()
    {
        HasSavedPlacement = HasSavedPlacement,
        Left = Left,
        Top = Top,
        Width = Width,
        Height = Height,
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
        HideAntigravityClaudeAndGptModels = HideAntigravityClaudeAndGptModels,
        HideAntigravityFiveHourLimits = HideAntigravityFiveHourLimits,
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

    private void Set<T>(T current, T value, Action<T> assign)
    {
        if (EqualityComparer<T>.Default.Equals(current, value))
        {
            return;
        }

        assign(value);
        SaveAndNotify();
    }

    private static bool AreValidUsageColors(
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
        new UsageColorConverter().TryConfigure(
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

    private static double NormalizePositive(double value, double fallback) =>
        double.IsFinite(value) && value >= 1 ? value : fallback;

    private static string NormalizeFontSizePreset(string? value) => value switch
    {
        "Compact" or "Small" or "Normal" or "Large" or "Extra Large" => value,
        _ => "Normal"
    };

    private static string NormalizeWidgetFont(string? value) => value switch
    {
        "Segoe UI Variable Text" or "VT323" or "Pixelify Sans" or "Silkscreen" or "Tiny5" or
        "Space Mono" or "Chakra Petch" or "IBM Plex Mono" or "DotGothic16" or "Handjet" or
        "Rajdhani" or "Oxanium" or "Kode Mono" => value,
        _ => "Segoe UI Variable Text"
    };

    private static string NormalizeWidgetAppearance(string? value) => value == "Retro" ? "Retro" : "Default";

    private static string NormalizeWidgetTextWeight(string? value) => value switch
    {
        "SemiBold" or "Bold" => value,
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

    private sealed class WindowPlacement
    {
        public bool? HasSavedPlacement { get; init; }
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
        public bool HideAntigravityClaudeAndGptModels { get; init; }
        public bool HideAntigravityFiveHourLimits { get; init; }
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
}
