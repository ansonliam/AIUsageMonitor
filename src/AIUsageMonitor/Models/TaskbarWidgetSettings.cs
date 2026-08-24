namespace AIUsageMonitor.Models;

public sealed class TaskbarWidgetSettings
{
    public bool ShowTaskbarWidget { get; init; }
    public bool ShowCodexOnTaskbar { get; init; } = true;
    public bool ShowClaudeOnTaskbar { get; init; } = true;
    public bool ShowAntigravityOnTaskbar { get; init; } = true;
    public bool ShowCursorOnTaskbar { get; init; } = true;
}
