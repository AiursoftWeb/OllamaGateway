// ReSharper disable all
using Aiursoft.OllamaGateway.Services;
using Aiursoft.OllamaGateway.Services.Clickhouse;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace Aiursoft.OllamaGateway.Tests;

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
    }
}
