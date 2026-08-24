namespace AIUsageMonitor.Models;

public sealed class TaskbarWidgetSettings
{
    public bool ShowTaskbarWidget { get; init; }
    public bool ShowCodexOnTaskbar { get; init; } = true;
    public bool ShowClaudeOnTaskbar { get; init; } = true;
    public bool ShowAntigravityOnTaskbar { get; init; } = true;
    public bool ShowCursorOnTaskbar { get; init; } = true;
    // 12 roughly matches the size Windows itself uses for the language indicator ("ENG US") and
    // clock text in the tray, so the widget reads consistently with its neighbours by default.
    public double TaskbarFontSize { get; init; } = 12;
    public string TaskbarFont { get; init; } = "Segoe UI Variable Text";
    public string TaskbarTextWeight { get; init; } = "SemiBold";

    // Retained for compatibility with existing settings files. Shared window stages overwrite
    // these values whenever the app starts or stages are changed.
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
}
