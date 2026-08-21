namespace AIUsageMonitor.Services;

public interface IApplicationController
{
    void ShowMainWindow();
    void ShowSettings();
    Task RefreshAllAsync();
    Task ExitAsync();
}
