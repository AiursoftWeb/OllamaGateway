using Aiursoft.Canon.BackgroundJobs;

namespace Aiursoft.OllamaGateway.Services.Clickhouse;

public class ClickhouseFlushService(
    RequestLogBuffer buffer,
    ClickhouseDbContext clickhouseDbContext,
    ILogger<ClickhouseFlushService> logger) : IBackgroundJob
{
    public string Name => "ClickHouse Log Flush";
    public string Description => "Drains the in-memory request log buffer and writes batches to ClickHouse.";

    public async Task ExecuteAsync()
    {
        if (!clickhouseDbContext.Enabled) return;

        var batch = new List<Entities.RequestLog>();
        var count = buffer.Drain(batch);

        if (count == 0) return;

        foreach (var log in batch)
            clickhouseDbContext.RequestLogs.Add(log);

        await clickhouseDbContext.SaveChangesAsync();
        logger.LogInformation("Flushed {Count} request logs to ClickHouse", count);
    }
}
