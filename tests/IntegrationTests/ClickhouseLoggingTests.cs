using System.Net;
using Aiursoft.DbTools;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Services;
using Aiursoft.OllamaGateway.Services.Clickhouse;
using static Aiursoft.WebTools.Extends;
using Moq;

namespace Aiursoft.OllamaGateway.Tests.IntegrationTests;

[TestClass]
public class ClickhouseLoggingTests : TestBase
{
    [TestInitialize]
    public override async Task CreateServer()
    {
        // Setup mocks before creating server
        TestStartup.MockClickhouse.Reset();
        // ENABLE Clickhouse for these tests
        TestStartup.MockClickhouse.Setup(c => c.Enabled).Returns(true);

        TestStartup.MockOllamaService.Reset();
        TestStartup.MockOllamaService.Setup(s => s.GetUnderlyingModelsAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<string> { "llama3.2" });

        MockUpstreamState.Reset();

        Server = await AppAsync<TestStartup>([], port: Port);
        await Server.UpdateDbAsync<TemplateDbContext>();
        await Server.SeedAsync();
        await Server.StartAsync();

        // Enable anonymous access for simplicity in testing
        using (var scope = Server.Services.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<GlobalSettingsService>();
            await settings.UpdateSettingAsync(Configuration.SettingsMap.AllowAnonymousApiCall, "True");
        }
    }

    [TestMethod]
    public async Task Fallback_AttributesLogUsageAndRecentRequestToActualAttempts()
    {
        int primaryProviderId;
        int fallbackProviderId;
        int fallbackBackendId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var primaryProvider = new OllamaProvider
            {
                Name = "Primary Tracking Provider",
                BaseUrl = "http://tracking-primary.test:11434"
            };
            var fallbackProvider = new OllamaProvider
            {
                Name = "Fallback Tracking Provider",
                BaseUrl = "http://tracking-fallback.test:11434"
            };
            db.OllamaProviders.AddRange(primaryProvider, fallbackProvider);
            await db.SaveChangesAsync();
            primaryProviderId = primaryProvider.Id;
            fallbackProviderId = fallbackProvider.Id;

            var virtualModel = new VirtualModel
            {
                Name = "tracking-model",
                Type = ModelType.Chat,
                SelectionStrategy = SelectionStrategy.PriorityFallback,
                MaxRetries = 2
            };
            virtualModel.VirtualModelBackends.Add(new VirtualModelBackend
            {
                ProviderId = primaryProvider.Id,
                UnderlyingModelName = "primary-model",
                Priority = 0,
                Enabled = true,
                IsHealthy = true
            });
            var fallbackBackend = new VirtualModelBackend
            {
                ProviderId = fallbackProvider.Id,
                UnderlyingModelName = "fallback-model",
                Priority = 1,
                Enabled = true,
                IsHealthy = true
            };
            virtualModel.VirtualModelBackends.Add(fallbackBackend);
            db.VirtualModels.Add(virtualModel);
            await db.SaveChangesAsync();
            fallbackBackendId = fallbackBackend.Id;
        }

        MockUpstreamState.Handler = (request, _) =>
        {
            if (request.RequestUri?.Host == "tracking-primary.test")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("{\"error\":\"temporary failure\"}")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"done\":true,\"model\":\"fallback-model\",\"message\":{\"content\":\"hi\"}}")
            });
        };

        var buffer = Server!.Services.GetRequiredService<RequestLogBuffer>();
        buffer.Drain([]);
        var response = await Http.PostAsync(
            "/api/chat",
            new StringContent("{\"model\":\"tracking-model\",\"messages\":[]}", System.Text.Encoding.UTF8, "application/json"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var logs = new List<RequestLog>();
        buffer.Drain(logs);
        Assert.AreEqual(1, logs.Count);
        Assert.AreEqual(fallbackBackendId, logs[0].BackendId);
        Assert.AreEqual(fallbackProviderId, logs[0].ProviderId);
        Assert.AreEqual("fallback-model", logs[0].UnderlyingModelName);

        var activeTracker = Server.Services.GetRequiredService<ActiveRequestTracker>();
        Assert.AreEqual(0, activeTracker.GetBusyPhysicalModels().Count);
        Assert.AreEqual("fallback-model", activeTracker.GetRecentRequests()[0].BackendModelName);

        var usageCounter = Server.Services.GetRequiredService<UsageCounter>();
        var (modelUsages, _) = usageCounter.SwapModelBuffers();
        Assert.AreEqual(1L, modelUsages[(primaryProviderId, "primary-model")]);
        Assert.AreEqual(1L, modelUsages[(fallbackProviderId, "fallback-model")]);
    }

    [TestMethod]
    public async Task TestClickhouseLoggingFilter()
    {
        // 1. Prepare a virtual model
        int providerId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var provider = new OllamaProvider { Name = "Provider", BaseUrl = "http://localhost:11434" };
            db.OllamaProviders.Add(provider);
            await db.SaveChangesAsync();
            providerId = provider.Id;
            var virtualModel = new VirtualModel
            {
                Name = "chat-model",
                Type = ModelType.Chat
            };
            virtualModel.VirtualModelBackends.Add(new VirtualModelBackend
            {
                ProviderId = provider.Id,
                UnderlyingModelName = "llama3.2",
                Enabled = true,
                IsHealthy = true
            });
            db.VirtualModels.Add(virtualModel);
            await db.SaveChangesAsync();
        }

        // 2. Mock upstream
        MockUpstreamState.Handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"done\": true, \"model\": \"llama3.2\", \"message\": {\"content\": \"hi\"}}")
        });

        var buffer = Server!.Services.GetRequiredService<RequestLogBuffer>();

        // 3. Make an AI request
        var aiRequest = new HttpRequestMessage(HttpMethod.Post, "/api/chat");
        aiRequest.Content = new StringContent("{\"model\": \"chat-model\"}", System.Text.Encoding.UTF8, "application/json");
        var aiResponse = await Http.SendAsync(aiRequest);
        Assert.AreEqual(HttpStatusCode.OK, aiResponse.StatusCode);

        // Verify it was enqueued to the buffer
        var batch = new List<RequestLog>();
        var drained = buffer.Drain(batch);
        Assert.AreEqual(1, drained);

        // 4. Make a non-AI request (Home page)
        var homeResponse = await Http.GetAsync("/");
        Assert.AreEqual(HttpStatusCode.OK, homeResponse.StatusCode);

        // Verify home page was NOT enqueued
        var batch2 = new List<RequestLog>();
        buffer.Drain(batch2);
        Assert.AreEqual(0, batch2.Count);

        // 5. Make another AI request (OpenAI style)
        var oaiRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions");
        oaiRequest.Content = new StringContent("{\"model\": \"chat-model\", \"messages\":[]}", System.Text.Encoding.UTF8, "application/json");
        var oaiResponse = await Http.SendAsync(oaiRequest);
        Assert.AreEqual(HttpStatusCode.OK, oaiResponse.StatusCode);

        // Verify it was enqueued to the buffer
        var batch3 = new List<RequestLog>();
        buffer.Drain(batch3);
        Assert.AreEqual(1, batch3.Count);

        // Anthropic uses the same attempt tracker and must populate the existing
        // provider/model columns without requiring a ClickHouse schema change.
        var anthropicRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/messages");
        anthropicRequest.Content = new StringContent(
            "{\"model\":\"chat-model\",\"max_tokens\":16,\"messages\":[]}",
            System.Text.Encoding.UTF8,
            "application/json");
        var anthropicResponse = await Http.SendAsync(anthropicRequest);
        Assert.AreEqual(HttpStatusCode.OK, anthropicResponse.StatusCode);

        var anthropicBatch = new List<RequestLog>();
        buffer.Drain(anthropicBatch);
        Assert.AreEqual(1, anthropicBatch.Count);
        Assert.AreEqual(providerId, anthropicBatch[0].ProviderId);
        Assert.AreEqual("llama3.2", anthropicBatch[0].UnderlyingModelName);

        // 6. Flush buffer to ClickHouse and verify SaveChangesAsync is called
        using (var scope = Server.Services.CreateScope())
        {
            var flushService = scope.ServiceProvider.GetRequiredService<ClickhouseFlushService>();
            // Re-enqueue the logs for the flush test
            foreach (var log in batch)
                buffer.Enqueue(log);
            foreach (var log in batch3)
                buffer.Enqueue(log);
            foreach (var log in anthropicBatch)
                buffer.Enqueue(log);

            await flushService.ExecuteAsync();
        }

        TestStartup.MockClickhouse.Verify(c => c.SaveChangesAsync(), Times.Once);
    }
}
