using Aiursoft.ClickhouseSdk;
using Aiursoft.ClickhouseSdk.Abstractions;
using Aiursoft.ClickhouseLoggerProvider;
using Aiursoft.OllamaGateway.Entities;
using Microsoft.Extensions.Options;

namespace Aiursoft.OllamaGateway.Extensions;

public static class ClickhouseExtensions
{
    public static async Task InitClickhouseAsync(this IHost host)
    {
        using var scope = host.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<ClickhouseOptions>>();

        if (!options.CurrentValue.Enabled)
        {
            return;
        }

        await host.Services.InitClickhouseTableAsync<RequestLog>(options.CurrentValue.TableName, "RequestTime");
        await host.Services.InitLoggingTableAsync();
    }
}
