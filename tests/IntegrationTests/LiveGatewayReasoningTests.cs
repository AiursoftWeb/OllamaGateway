using System.Text;
using System.Text.Json.Nodes;
using Aiursoft.DbTools;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Services;
using Microsoft.Extensions.Http;
using Moq;
using static Aiursoft.WebTools.Extends;

namespace Aiursoft.OllamaGateway.Tests.IntegrationTests;

public class LiveGatewayStartup : TestStartup
{
    public override void ConfigureServices(IConfiguration configuration, IWebHostEnvironment environment, IServiceCollection services)
    {
        base.ConfigureServices(configuration, environment, services);
        services.ConfigureAll<HttpClientFactoryOptions>(options =>
            options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = new SocketsHttpHandler()));
    }
}

[TestClass]
public class LiveGatewayReasoningTests : TestBase
{
    private const string VirtualModelName = "live-reasoning-check:latest";

    [TestInitialize]
    public override async Task CreateServer()
    {
        var baseUrl = Environment.GetEnvironmentVariable("OLLAMAGATEWAY_LIVE_BASE_URL");
        var model = Environment.GetEnvironmentVariable("OLLAMAGATEWAY_LIVE_MODEL");
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model))
        {
            Assert.Inconclusive("Set OLLAMAGATEWAY_LIVE_BASE_URL and OLLAMAGATEWAY_LIVE_MODEL to run live gateway tests.");
            return;
        }

        TestStartup.MockClickhouse.Reset();
        TestStartup.MockClickhouse.Setup(client => client.Enabled).Returns(false);
        Server = await AppAsync<LiveGatewayStartup>([], port: Port);
        await Server.UpdateDbAsync<TemplateDbContext>();
        await Server.SeedAsync();
        await Server.StartAsync();

        using var scope = Server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<GlobalSettingsService>();
        await settings.UpdateSettingAsync(Configuration.SettingsMap.AllowAnonymousApiCall, "True");

        var provider = new OllamaProvider
        {
            Name = "Live OpenAI compatible provider",
            BaseUrl = baseUrl,
            ProviderType = ProviderType.OpenAI,
            SupportsOpenAiChatCompletions = true,
            SupportsOpenAiResponses = false
        };
        db.OllamaProviders.Add(provider);
        await db.SaveChangesAsync();

        var virtualModel = new VirtualModel { Name = VirtualModelName, Type = ModelType.Chat, NumPredict = 64 };
        virtualModel.VirtualModelBackends.Add(new VirtualModelBackend
        {
            ProviderId = provider.Id,
            UnderlyingModelName = model,
            Protocol = BackendProtocol.OpenAiChatCompletions,
            Enabled = true,
            IsHealthy = true,
            IsReady = true
        });
        db.VirtualModels.Add(virtualModel);
        await db.SaveChangesAsync();
    }

    [TestMethod]
    [TestCategory("Live")]
    [DataRow("/api/chat", false)]
    [DataRow("/v1/chat/completions", true)]
    public async Task LiveGateway_ForwardsThinkingBeforeFinalAnswer(string path, bool openAi)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Content = new StringContent(
            """{"model":"live-reasoning-check:latest","messages":[{"role":"user","content":"Reply with OK."}],"stream":true,"max_tokens":64}""",
            Encoding.UTF8,
            "application/json");

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var reader = new StreamReader(stream);
        while (true)
        {
            var line = await reader.ReadLineAsync(timeout.Token);
            Assert.IsNotNull(line, "Gateway closed the stream before sending reasoning.");
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (openAi)
            {
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                Assert.AreNotEqual("data: [DONE]", line, "Gateway completed without sending reasoning.");
            }

            var frame = JsonNode.Parse(openAi ? line["data: ".Length..] : line);
            var reasoning = openAi
                ? frame?["choices"]?[0]?["delta"]?["reasoning_content"]?.ToString()
                  ?? frame?["choices"]?[0]?["delta"]?["reasoning"]?.ToString()
                : frame?["message"]?["thinking"]?.ToString();
            if (!string.IsNullOrWhiteSpace(reasoning)) return;

            var content = openAi
                ? frame?["choices"]?[0]?["delta"]?["content"]?.ToString()
                : frame?["message"]?["content"]?.ToString();
            Assert.IsTrue(string.IsNullOrWhiteSpace(content), "The final answer arrived before reasoning.");
        }
    }
}
