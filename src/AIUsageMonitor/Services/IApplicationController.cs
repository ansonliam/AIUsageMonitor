namespace AIUsageMonitor.Services;

public interface IApplicationController
{
    void ShowMainWindow();
    void HideMainWindow();
    bool IsMainWindowVisible();
    void SetTaskbarWidgetVisibility(bool isVisible);
    bool IsTaskbarWidgetVisible();
    void ShowSettings();
    void ShowIconPreview();
    Task RefreshAllAsync();
    Task ExitAsync();
}
