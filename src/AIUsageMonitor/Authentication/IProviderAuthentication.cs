namespace AIUsageMonitor.Authentication;

public interface IProviderAuthentication
{
    bool IsAuthenticated { get; }
    Task RefreshAuthenticationStateAsync(CancellationToken cancellationToken = default);
    Task StartLoginAsync(CancellationToken cancellationToken = default);
}
