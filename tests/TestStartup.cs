// ReSharper disable all
using Aiursoft.Canon.TaskQueue;
using Aiursoft.OllamaGateway.Services;
using Aiursoft.OllamaGateway.Services.BackgroundJobs;
using Aiursoft.OllamaGateway.Services.Clickhouse;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;

namespace Aiursoft.OllamaGateway.Tests;

/// <summary>
/// Static holder for mock upstream state. Separating state from the handler 
/// avoids DelegatingHandler reuse issues with HttpMessageHandlerBuilder.
/// </summary>
public static class MockUpstreamState
{
    /// <summary>
    /// Set this in your test to control what the upstream "Ollama" returns.
    /// </summary>
    public static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Handler { get; set; }

    /// <summary>
    /// Captured last request forwarded upstream.
    /// </summary>
    public static HttpRequestMessage? LastRequest { get; set; }
    public static string? LastRequestBody { get; set; }

    public static void Reset()
    {
        Handler = null;
        LastRequest = null;
        LastRequestBody = null;
    }
}

/// <summary>
/// A fresh HttpMessageHandler created each time by the factory.
/// Delegates to the static MockUpstreamState for behavior and capture.
/// </summary>
public class MockUpstreamHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        MockUpstreamState.LastRequest = request;
        if (request.Content != null)
        {
            MockUpstreamState.LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        if (MockUpstreamState.Handler != null)
        {
            return await MockUpstreamState.Handler(request, cancellationToken);
        }

        // Default: return 502 Bad Gateway if no handler is configured
        return new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway)
        {
            Content = new StringContent("No mock handler configured for upstream request.")
        };
    }
}

public class TestStartup : Startup
{
    public static StubOllamaService OllamaStub { get; } = new();
    public static StubClickhouseDbContext ClickhouseStub { get; } = new();

    public override void ConfigureServices(IConfiguration configuration, IWebHostEnvironment environment, IServiceCollection services)
    {
        base.ConfigureServices(configuration, environment, services);

        // Use TryAdd to ensure our mocks take precedence if possible, or RemoveAll then Add
        services.RemoveAll<OllamaService>();
        services.AddScoped<OllamaService>(_ => OllamaStub);

        services.RemoveAll<ClickhouseDbContext>();
        services.AddScoped<ClickhouseDbContext>(_ => ClickhouseStub);

        // Completely disable all background services for maximum speed and stability
        services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>();

        // Replace PrimaryHandler with our mock so all outbound HTTP is intercepted.
        // A new MockUpstreamHandler instance is created each time, avoiding reuse errors.
        services.ConfigureAll<HttpClientFactoryOptions>(options =>
        {
            options.HttpMessageHandlerBuilderActions.Add(builder =>
            {
                builder.PrimaryHandler = new MockUpstreamHandler();
            });
        });
    }
}

/// <summary>
/// TestStartup variant used for background job tests (queue engine is already registered by main Startup).
/// All other background services (health monitor, warmup, usage flush) remain disabled.
/// </summary>
public class TestStartupWithQueue : TestStartup
{
    public override void ConfigureServices(IConfiguration configuration, IWebHostEnvironment environment, IServiceCollection services)
    {
        base.ConfigureServices(configuration, environment, services);
        // Re-add the task queue worker since base removes all IHostedService registrations
        services.AddTaskQueueEngine();
    }
}

public sealed class StubOllamaService() : OllamaService(null!, null!)
{
    public List<string>? UnderlyingModels { get; set; }
    public List<OllamaModel>? DetailedModels { get; set; }
    public List<OllamaRunningModel>? RunningModels { get; set; }
    public List<string>? OpenAIModels { get; set; }
    public int DetailedModelsCalls { get; private set; }
    public int RunningModelsCalls { get; private set; }

    public void Reset()
    {
        UnderlyingModels = null;
        DetailedModels = null;
        RunningModels = null;
        OpenAIModels = null;
        ClearCalls();
    }

    public void ClearCalls()
    {
        DetailedModelsCalls = 0;
        RunningModelsCalls = 0;
    }

    public override Task<List<string>?> GetUnderlyingModelsAsync(string baseUrl, string? bearerToken = null, int timeoutSeconds = 60)
        => Task.FromResult(UnderlyingModels);

    public override Task<List<OllamaModel>?> GetDetailedModelsAsync(string baseUrl, string? bearerToken = null, int timeoutSeconds = 60)
    {
        DetailedModelsCalls++;
        return Task.FromResult(DetailedModels);
    }

    public override Task<List<OllamaRunningModel>?> GetRunningModelsAsync(string baseUrl, string? bearerToken = null, int timeoutSeconds = 60)
    {
        RunningModelsCalls++;
        return Task.FromResult(RunningModels);
    }

    public override Task<string?> GetVersionAsync(string baseUrl, string? bearerToken = null)
        => Task.FromResult<string?>(null);

    public override Task<List<string>?> GetOpenAIAvailableModelsAsync(string baseUrl, string? bearerToken = null)
        => Task.FromResult(OpenAIModels);
}

public sealed class StubClickhouseDbContext : ClickhouseDbContext
{
    public bool IsEnabled { get; set; }
    public override bool Enabled => IsEnabled;
    public int SaveChangesCalls { get; private set; }

    public void Reset()
    {
        IsEnabled = false;
        ClearCalls();
    }

    public void ClearCalls() => SaveChangesCalls = 0;

    public override Task SaveChangesAsync()
    {
        SaveChangesCalls++;
        return Task.CompletedTask;
    }
}
