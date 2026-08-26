namespace AIUsageMonitor.Models;

public sealed class TaskbarWidgetSettings
{
    public bool ShowTaskbarWidget { get; init; } = true;
    // False marks legacy settings written before monitor selection existed. Those keep the primary
    // taskbar enabled when migrated; an empty list with this true intentionally means none.
    public bool HasTaskbarMonitorSelection { get; init; }
    public List<string> EnabledTaskbarMonitorIds { get; init; } = [];
    public bool SyncTaskbarMonitorAppearance { get; init; }
    public List<TaskbarMonitorAppearanceSettings> TaskbarMonitorAppearances { get; init; } = [];
    public bool ShowCodexOnTaskbar { get; init; } = true;
    public bool ShowClaudeOnTaskbar { get; init; } = true;
    public bool ShowAntigravityOnTaskbar { get; init; } = true;
    public bool ShowCursorOnTaskbar { get; init; } = true;
    // 12 roughly matches the size Windows itself uses for the language indicator ("ENG US") and
    // clock text in the tray, so the widget reads consistently with its neighbours by default.
    public double TaskbarFontSize { get; init; } = 13;
    public double TaskbarIconSize { get; init; } = 14;
    public string TaskbarFont { get; init; } = "Chakra Petch";
    public string TaskbarTextWeight { get; init; } = "Regular";
    public double TaskbarTextVerticalOffset { get; init; }

    // Retained for compatibility with existing settings files. Shared window stages overwrite
    // these values whenever the app starts or stages are changed.
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
}

public sealed class TaskbarMonitorAppearanceSettings
{
    public string MonitorId { get; init; } = string.Empty;
    public double TextSize { get; init; }
    public double IconSize { get; init; }
    public double TextVerticalOffset { get; init; }
    // Nullable distinguishes older settings with no offset from an intentional 0px offset.
    public double? RightOffset { get; init; }
    // Stored separately from RightOffset: left and right alignment each remember their own
    // offset, so switching sides doesn't carry one over as the other's value. Defaults to 0 -
    // left alignment starts flush with the taskbar's left edge.
    public double? LeftOffset { get; init; }
    // "Left" or "Right". Defaults to "Right" so settings files written before per-monitor
    // alignment existed keep their original placement (flush against the tray, or offset in from
    // the right edge on a secondary monitor).
    public string Alignment { get; init; } = "Right";
}
