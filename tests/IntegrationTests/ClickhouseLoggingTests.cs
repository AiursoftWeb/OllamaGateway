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
    public async Task TestClickhouseLoggingFilter()
    {
        // 1. Prepare a virtual model
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var provider = new OllamaProvider { Name = "Provider", BaseUrl = "http://localhost:11434" };
            db.OllamaProviders.Add(provider);
            await db.SaveChangesAsync();
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

        // 6. Flush buffer to ClickHouse and verify SaveChangesAsync is called
        using (var scope = Server.Services.CreateScope())
        {
            var flushService = scope.ServiceProvider.GetRequiredService<ClickhouseFlushService>();
            // Re-enqueue the logs for the flush test
            foreach (var log in batch)
                buffer.Enqueue(log);
            foreach (var log in batch3)
                buffer.Enqueue(log);

            await flushService.ExecuteAsync();
        }

        TestStartup.MockClickhouse.Verify(c => c.SaveChangesAsync(), Times.Once);
    }
}
