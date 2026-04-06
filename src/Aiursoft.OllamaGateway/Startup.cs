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

using Aiursoft.ClickhouseLoggerProvider;
using Aiursoft.ClickhouseSdk.Abstractions;
using Aiursoft.Canon.TaskQueue;
using Aiursoft.Canon.BackgroundJobs;
using Aiursoft.Canon.ScheduledTasks;

namespace Aiursoft.OllamaGateway;

[ExcludeFromCodeCoverage]
public class Startup : IWebStartup
{
    public virtual void ConfigureServices(IConfiguration configuration, IWebHostEnvironment environment, IServiceCollection services)
    {
        // AppSettings.
        services.Configure<AppSettings>(configuration.GetSection("AppSettings"));
        services.Configure<ClickhouseOptions>(configuration.GetSection("Clickhouse"));
        services.AddLogging(builder => 
        {
            builder.AddClickhouse(options => configuration.GetSection("Logging:Clickhouse").Bind(options));
        });

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
        services.AddAssemblyDependencies(typeof(Startup).Assembly);
        services.AddSingleton<NavigationState<Startup>>();
        services.AddScoped<Models.RequestLogContext>();

        // Background job queue
        services.AddTaskQueueEngine();
        services.AddScheduledTaskEngine();
        services.RegisterBackgroundJob<Services.BackgroundJobs.DummyJob>();
        var orphanAvatarCleanupJob = services.RegisterBackgroundJob<Services.BackgroundJobs.OrphanAvatarCleanupJob>();
        services.RegisterScheduledTask(registration: orphanAvatarCleanupJob, period: TimeSpan.FromHours(6), startDelay: TimeSpan.FromMinutes(5));
        var backendHealthJob = services.RegisterBackgroundJob<Services.BackgroundJobs.BackendHealthMonitor>();
        services.RegisterScheduledTask(registration: backendHealthJob, period: TimeSpan.FromMinutes(1), startDelay: TimeSpan.FromSeconds(30));
        var modelWarmupJob = services.RegisterBackgroundJob<Services.BackgroundJobs.ModelWarmupService>();
        services.RegisterScheduledTask(registration: modelWarmupJob, period: TimeSpan.FromMinutes(5), startDelay: TimeSpan.FromMinutes(1));
        var usageFlushJob = services.RegisterBackgroundJob<Services.BackgroundJobs.UsageFlushService>();
        services.RegisterScheduledTask(registration: usageFlushJob, period: TimeSpan.FromMinutes(3), startDelay: TimeSpan.FromSeconds(30));

        // Controllers and localization
        //
        // JSON SERIALIZER ARCHITECTURE — READ BEFORE CHANGING:
        //
        // This project intentionally uses TWO JSON libraries for different stages of the pipeline:
        //
        //   1. Newtonsoft.Json  — handles INBOUND deserialization only.
        //      ASP.NET Core's [FromBody] model binding invokes Newtonsoft when a controller
        //      action receives a request. DefaultContractResolver is used so that property names
        //      stay as-is (no camelCase transform by Newtonsoft itself).
        //      IMPORTANT: Because Newtonsoft matches JSON keys case-insensitively but does NOT
        //      ignore underscores, every snake_case field on a model class that is bound via
        //      [FromBody] MUST carry a [Newtonsoft.Json.JsonProperty("snake_case_name")] attribute.
        //      Without it, keys like "num_ctx" silently fail to bind to "NumCtx".
        //      See OllamaRequestOptions in ProxyController.cs for the canonical example.
        //
        //   2. System.Text.Json — handles OUTBOUND serialization and all streaming parse/transform.
        //      ProxyController and OpenAIController use STJ (JsonSerializer + JsonNode) to:
        //        a) Serialize the request forwarded to Ollama/OpenAI (SnakeCaseLower policy).
        //        b) Parse and mutate NDJSON/SSE stream chunks on the fly (JsonNode mutable DOM).
        //      STJ ignores [Newtonsoft.Json.JsonProperty] entirely, so the two libraries
        //      do not interfere with each other.
        //
        // DO NOT unify to a single library without carefully auditing every [FromBody] binding
        // and every JsonNode streaming transformation — the two stages have incompatible
        // requirements (mutable DOM vs. schema-driven binding with snake_case contracts).
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
        app.UseStatusCodePagesWithReExecute("/Error/Code{0}");
        app.UseStaticFiles();
        app.UseRequestLogging();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapDefaultControllerRoute();
    }
}
