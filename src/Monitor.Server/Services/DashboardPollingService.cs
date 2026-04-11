namespace Monitor.Server.Services;

public sealed class DashboardPollingService(
    IConfiguration configuration,
    DashboardSnapshotBuilder snapshotBuilder,
    DashboardSocketServer socketServer,
    ILogger<DashboardPollingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var refreshMs = configuration.GetValue<int>("Dashboard:RefreshIntervalMs", 750);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = await snapshotBuilder.BuildAsync(stoppingToken);
                await socketServer.BroadcastSnapshotAsync(snapshot, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Dashboard polling iteration failed.");
            }

            await Task.Delay(refreshMs, stoppingToken);
        }
    }
}
