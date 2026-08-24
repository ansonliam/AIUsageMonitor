namespace AIUsageMonitor.Services;

public interface IApplicationController
{
    void ShowMainWindow();
    void HideMainWindow();
    void SetTaskbarWidgetVisibility(bool isVisible);
    void ShowSettings();
    void ShowIconPreview();
    Task RefreshAllAsync();
    Task ExitAsync();
}
