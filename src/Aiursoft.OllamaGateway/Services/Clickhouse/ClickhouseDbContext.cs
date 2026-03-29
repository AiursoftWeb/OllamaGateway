using Aiursoft.ClickhouseSdk;
using Aiursoft.ClickhouseSdk.Abstractions;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.Scanner.Abstractions;
using Microsoft.Extensions.Options;

namespace Aiursoft.OllamaGateway.Services.Clickhouse;

public class ClickhouseDbContext : Aiursoft.ClickhouseSdk.ClickhouseDbContext, IScopedDependency
{
    public ClickhouseSet<RequestLog> RequestLogs { get; }

    // For Moq
    protected ClickhouseDbContext() : base(new DummyOptionsMonitor())
    {
        RequestLogs = null!;
    }

    private class DummyOptionsMonitor : IOptionsMonitor<ClickhouseOptions>
    {
        public ClickhouseOptions Get(string? name) => new();
        public IDisposable? OnChange(Action<ClickhouseOptions, string?> listener) => null;
        public ClickhouseOptions CurrentValue => new();
    }

    public ClickhouseDbContext(IOptionsMonitor<ClickhouseOptions> options) : base(options)
    {
        RequestLogs = new ClickhouseSet<RequestLog>(GetConnection, options.CurrentValue.TableName, log => new object[] 
        {
            log.IP,
            log.ConversationMessageCount,
            log.LastQuestion,
            log.Model,
            log.Success ? 1 : 0,
            log.Duration,
            log.Thinking,
            log.Answer,
            log.RequestTime,
            log.Method,
            log.Path,
            log.StatusCode,
            log.UserAgent,
            log.TraceId,
            log.PromptTokens,
            log.CompletionTokens,
            log.TotalTokens,
            log.UserId,
            log.ApiKeyName,
            log.BackendId ?? 0
        });
    }

    public override async Task SaveChangesAsync()
    {
        await RequestLogs.SaveChangesAsync();
    }
}
