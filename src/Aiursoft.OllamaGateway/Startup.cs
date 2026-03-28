using Aiursoft.CSTools.Tools;
using Aiursoft.DbTools.Switchable;
using Aiursoft.Scanner;
using Aiursoft.OllamaGateway.Configuration;
using Aiursoft.WebTools.Abstractions.Models;
using Aiursoft.OllamaGateway.InMemory;
using Aiursoft.OllamaGateway.MySql;
using Aiursoft.OllamaGateway.Services.Authentication;
using Aiursoft.OllamaGateway.Sqlite;
using Aiursoft.OllamaGateway.Middlewares;
using Aiursoft.UiStack.Layout;
using Aiursoft.UiStack.Navigation;
using Microsoft.AspNetCore.Mvc.Razor;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Diagnostics.CodeAnalysis;

using Aiursoft.OllamaGateway.Models.Configuration;

namespace Aiursoft.OllamaGateway;

[ExcludeFromCodeCoverage]
public class Startup : IWebStartup
{
    public virtual void ConfigureServices(IConfiguration configuration, IWebHostEnvironment environment, IServiceCollection services)
    {
        // AppSettings.
        services.Configure<AppSettings>(configuration.GetSection("AppSettings"));
        services.Configure<ClickhouseOptions>(configuration.GetSection("Clickhouse"));

        // Relational database
        var (connectionString, dbType, allowCache) = configuration.GetDbSettings();
        services.AddSwitchableRelationalDatabase(
            dbType: EntryExtends.IsInUnitTests() ? "InMemory" : dbType,
            connectionString: connectionString,
            supportedDbs:
            [
                new MySqlSupportedDb(allowCache: allowCache, splitQuery: false),
                new SqliteSupportedDb(allowCache: allowCache, splitQuery: true),
                new InMemorySupportedDb()
            ]);

        // Authentication and Authorization
        services.AddTemplateAuth(configuration);

        // Services
        services.AddMemoryCache();
        services.AddHttpClient();
        services.AddSingleton<Services.IModelSelector, Services.ModelSelector>();
        services.AddScoped<Services.IModelSelectionService, Services.ModelSelectionService>();
        
        services.AddScoped<Services.Proxy.Handlers.IModelsInfoService, Services.Proxy.Handlers.ModelsInfoService>();
        services.AddScoped<Services.Proxy.Handlers.IOllamaChatHandler, Services.Proxy.Handlers.OllamaChatHandler>();
        services.AddScoped<Services.Proxy.Handlers.IOllamaEmbeddingHandler, Services.Proxy.Handlers.OllamaEmbeddingHandler>();
        services.AddScoped<Services.Proxy.Handlers.IOpenAIChatHandler, Services.Proxy.Handlers.OpenAIChatHandler>();
        services.AddScoped<Services.Proxy.IUpstreamExecutor, Services.Proxy.UpstreamExecutor>();
        services.AddScoped<Services.Proxy.Formatters.OllamaResponseFormatter>();
        services.AddScoped<Services.Proxy.Formatters.OpenAIResponseFormatter>();
        services.AddScoped<Services.Proxy.ProxyTelemetryService>();
        
        services.AddAssemblyDependencies(typeof(Startup).Assembly);
        services.AddSingleton<NavigationState<Startup>>();
        services.AddScoped<Models.RequestLogContext>();

        // Background job queue
        services.AddSingleton<Services.BackgroundJobs.BackgroundJobQueue>();
        services.AddHostedService<Services.BackgroundJobs.QueueWorkerService>();
        services.AddHostedService<Services.BackgroundJobs.BackendHealthMonitor>();
        services.AddHostedService<Services.BackgroundJobs.ModelWarmupService>();
        services.AddHostedService<Services.BackgroundJobs.UsageFlushService>();

        // Controllers and localization
        services.AddControllersWithViews()
            .AddNewtonsoftJson(options =>
            {
                options.SerializerSettings.DateTimeZoneHandling = DateTimeZoneHandling.Utc;
                options.SerializerSettings.ContractResolver = new DefaultContractResolver();
            })
            .AddApplicationPart(typeof(Startup).Assembly)
            .AddApplicationPart(typeof(UiStackLayoutViewModel).Assembly)
            .AddViewLocalization(LanguageViewLocationExpanderFormat.Suffix)
            .AddDataAnnotationsLocalization();
    }

    public virtual void Configure(WebApplication app)
    {
        app.UseExceptionHandler("/Error/Code500");
        app.UseOllamaExceptionHandler();
        app.UseStatusCodePagesWithReExecute("/Error/Code{0}");
        app.UseStaticFiles();
        app.UseRequestLogging();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapDefaultControllerRoute();
    }
}
