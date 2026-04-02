// ReSharper disable all
using Aiursoft.OllamaGateway.Services;
using Aiursoft.OllamaGateway.Services.BackgroundJobs;
using Aiursoft.OllamaGateway.Services.Clickhouse;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Moq;

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
    public static Mock<OllamaService> MockOllamaService { get; } = new(null!, null!, null!);
    public static Mock<ClickhouseDbContext> MockClickhouse { get; } = new();

    public override void ConfigureServices(IConfiguration configuration, IWebHostEnvironment environment, IServiceCollection services)
    {
        base.ConfigureServices(configuration, environment, services);
        
        services.RemoveAll<OllamaService>();
        services.AddScoped(_ => MockOllamaService.Object);

        services.RemoveAll<ClickhouseDbContext>();
        services.AddScoped(_ => MockClickhouse.Object);

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
/// TestStartup variant that re-enables the QueueWorkerService for background job tests.
/// All other background services (health monitor, warmup, usage flush) remain disabled.
/// </summary>
public class TestStartupWithQueue : TestStartup
{
    public override void ConfigureServices(IConfiguration configuration, IWebHostEnvironment environment, IServiceCollection services)
    {
        base.ConfigureServices(configuration, environment, services);
        services.AddHostedService<QueueWorkerService>();
    }
}
