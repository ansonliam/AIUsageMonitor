namespace AIUsageMonitor.Services;

public interface IApplicationController
{
    void ShowMainWindow();
    void ShowSettings();
    void ShowIconPreview();
    Task RefreshAllAsync();
    Task ExitAsync();
}
