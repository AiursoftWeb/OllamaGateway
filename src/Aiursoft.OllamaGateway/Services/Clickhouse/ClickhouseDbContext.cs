using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Models.Configuration;
using Aiursoft.Scanner.Abstractions;
using ClickHouse.Client.ADO;
using Microsoft.Extensions.Options;

namespace Aiursoft.OllamaGateway.Services.Clickhouse;

public class ClickhouseDbContext : IAsyncDisposable, IDisposable, IScopedDependency
{
    private ClickHouseConnection? _connection;
    private readonly ClickhouseOptions? _config;
    private readonly ILogger<ClickhouseDbContext>? _logger;

    public ClickhouseSet<RequestLog>? RequestLogs { get; }
    public virtual bool Enabled => _config?.Enabled ?? false;

    // For Moq
    protected ClickhouseDbContext()
    {
    }

    public ClickhouseDbContext(IOptionsMonitor<ClickhouseOptions> options, ILogger<ClickhouseDbContext> logger)
    {
        _config = options.CurrentValue;
        _logger = logger;
        RequestLogs = new ClickhouseSet<RequestLog>(GetConnection, "RequestLogs", log => new object[] 
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
            log.ApiKeyName
        });
    }

    private async Task<ClickHouseConnection> GetConnection()
    {
        if (_connection == null && _config != null)
        {
            _connection = new ClickHouseConnection(_config.ConnectionString);
            await _connection.OpenAsync();
        }
        return _connection ?? throw new InvalidOperationException("Connection not initialized.");
    }

    public virtual async Task SaveChangesAsync()
    {
        if (_config is not { Enabled: true } || RequestLogs == null)
        {
            return;
        }

        try
        {
            await RequestLogs.SaveChangesAsync();
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Failed to save logs to Clickhouse.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection != null)
        {
            await _connection.DisposeAsync();
        }
    }

    public void Dispose()
    {
        _connection?.Dispose();
    }
}
