using System.Net;
using System.Data.Common;
using Aiursoft.ClickhouseSdk.Abstractions;
using Aiursoft.OllamaGateway.Extensions;
using ClickHouse.Client.ADO;
using Aiursoft.DbTools;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Services;
using Aiursoft.OllamaGateway.Services.Clickhouse;
using static Aiursoft.WebTools.Extends;

namespace Aiursoft.OllamaGateway.Tests.IntegrationTests;

[TestClass]
public class ClickhouseLoggingTests : TestBase
{
    [TestInitialize]
    public override async Task CreateServer()
    {
        // Setup mocks before creating server
        TestStartup.ClickhouseStub.Reset();
        // ENABLE Clickhouse for these tests
        TestStartup.ClickhouseStub.IsEnabled = true;

        TestStartup.OllamaStub.Reset();
        TestStartup.OllamaStub.UnderlyingModels = new List<string> { "llama3.2" };

        MockUpstreamState.Reset();

        using var reservation = new TestPortReservation();
        Http.BaseAddress = new Uri($"http://localhost:{reservation.Port}");
        Server = await AppAsync<TestStartup>([], port: reservation.Port);
        try
        {
            reservation.ConfigureServer(Server);
            await Server.UpdateDbAsync<TemplateDbContext>();
            await Server.SeedAsync();
            await Server.StartAsync();
        }
        catch
        {
            Server.Dispose();
            Server = null;
            throw;
        }

        // Enable anonymous access for simplicity in testing
        using (var scope = Server.Services.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<GlobalSettingsService>();
            await settings.UpdateSettingAsync(Configuration.SettingsMap.AllowAnonymousApiCall, "True");
        }
    }

    [TestMethod]
    [DataRow("/api/chat", false)]
    [DataRow("/v1/chat/completions", false)]
    [DataRow("/api/embed", true)]
    [DataRow("/v1/embeddings", true)]
    public async Task SharedKeyKeepsConsumersSeparateWithoutChangingOwner(string path, bool embedding)
    {
        string ownerId;
        int keyId;
        var keyValue = Guid.NewGuid().ToString("N");
        var modelName = $"consumer-{Guid.NewGuid():N}";
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            ownerId = db.Users.First().Id;
            var key = new ApiKey
            {
                Name = "Consumer audit service",
                Key = keyValue,
                UserId = ownerId,
                ExpirationTime = DateTime.UtcNow.AddDays(1)
            };
            db.ApiKeys.Add(key);
            var model = new VirtualModel
            {
                Name = modelName,
                Type = embedding ? ModelType.Embedding : ModelType.Chat
            };
            model.VirtualModelBackends.Add(new VirtualModelBackend
            {
                Provider = new OllamaProvider { Name = "Consumer provider", BaseUrl = "http://localhost:11434" },
                UnderlyingModelName = "llama3.2",
                Enabled = true,
                IsHealthy = true
            });
            db.VirtualModels.Add(model);
            await db.SaveChangesAsync();
            keyId = key.Id;
        }
        MockUpstreamState.Handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(embedding
                ? "{\"model\":\"llama3.2\",\"embeddings\":[[0.1,0.2]]}"
                : "{\"done\":true,\"model\":\"llama3.2\",\"message\":{\"content\":\"hi\"}}")
        });

        var buffer = Server!.Services.GetRequiredService<RequestLogBuffer>();
        foreach (var consumer in new[] { "consumer-a", "consumer-b", "", new string('x', 129), "bad,id", "bad id" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", keyValue);
            request.Headers.TryAddWithoutValidation("X-Aiursoft-Consumer-Id", consumer);
            request.Content = new StringContent(
                $$"""{"model":"{{modelName}}","stream":false,"messages":[],"input":"test"}""",
                System.Text.Encoding.UTF8, "application/json");
            using var response = await Http.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var logs = new List<RequestLog>();
            buffer.Drain(logs);
            Assert.HasCount(1, logs);
            Assert.AreEqual(ownerId, logs[0].UserId);
            Assert.AreEqual(keyId, logs[0].ApiKeyId);
            Assert.AreEqual(consumer.StartsWith("consumer-") ? consumer : string.Empty, logs[0].ReportedConsumerId);
        }

        // Anonymous requests may still be allowed, but cannot claim a key's consumer namespace.
        using var anonymous = new HttpRequestMessage(HttpMethod.Post, path);
        anonymous.Headers.Add("X-Aiursoft-Consumer-Id", "consumer-a");
        anonymous.Content = new StringContent(
            $$"""{"model":"{{modelName}}","stream":false,"messages":[],"input":"test"}""",
            System.Text.Encoding.UTF8, "application/json");
        using var anonymousResponse = await Http.SendAsync(anonymous);
        Assert.AreEqual(HttpStatusCode.OK, anonymousResponse.StatusCode);
        var anonymousLogs = new List<RequestLog>();
        buffer.Drain(anonymousLogs);
        Assert.HasCount(1, anonymousLogs);
        Assert.AreEqual(0, anonymousLogs[0].ApiKeyId);
        Assert.AreEqual(string.Empty, anonymousLogs[0].ReportedConsumerId);
    }

    [TestMethod]
    public async Task InitializationRetainsRequestHistoryFor120DaysOnNewAndExistingTables()
    {
        var connectionString = Environment.GetEnvironmentVariable("CLICKHOUSE_CONNECTION");
        if (string.IsNullOrEmpty(connectionString))
            Assert.Inconclusive("Set CLICKHOUSE_CONNECTION to run the real ClickHouse retention test.");

        var database = $"gateway_retention_{Guid.NewGuid():N}";
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        builder["Database"] = database;
        using var host = Host.CreateDefaultBuilder().ConfigureServices(services =>
        {
            services.Configure<ClickhouseOptions>(options =>
            {
                options.Enabled = true;
                options.ConnectionString = builder.ConnectionString;
                options.TableName = "ModelRequests";
            });
        }).Build();
        await using var connection = new ClickHouseConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        try
        {
            await host.InitClickhouseAsync();
            command.CommandText = $"SHOW CREATE TABLE `{database}`.ModelRequests";
            StringAssert.Contains((await command.ExecuteScalarAsync())!.ToString()!, "TTL RequestTime + toIntervalDay(120)");

            // Simulate the deployed schema before consumer attribution existed.
            command.CommandText = $"ALTER TABLE `{database}`.ModelRequests DROP COLUMN ApiKeyId, DROP COLUMN ReportedConsumerId";
            await command.ExecuteNonQueryAsync();

            // Older installations created request tables without any TTL.
            command.CommandText = $"ALTER TABLE `{database}`.ModelRequests REMOVE TTL";
            await command.ExecuteNonQueryAsync();
            command.CommandText = $"""
                INSERT INTO `{database}`.ModelRequests (RequestTime, Model)
                VALUES (now() - INTERVAL 90 DAY, 'retained'), (now() - INTERVAL 121 DAY, 'expired')
                """;
            await command.ExecuteNonQueryAsync();
            await host.InitClickhouseAsync();
            await host.InitClickhouseAsync();
            command.CommandText = $"SHOW CREATE TABLE `{database}`.ModelRequests";
            StringAssert.Contains((await command.ExecuteScalarAsync())!.ToString()!, "TTL RequestTime + toIntervalDay(120)");
            command.CommandText = $"ALTER TABLE `{database}`.ModelRequests MATERIALIZE TTL SETTINGS mutations_sync = 2";
            await command.ExecuteNonQueryAsync();
            command.CommandText = $"SELECT Model FROM `{database}`.ModelRequests";
            Assert.AreEqual("retained", await command.ExecuteScalarAsync());
            command.CommandText = $"SELECT count() FROM `{database}`.ModelRequests";
            Assert.AreEqual(1UL, Convert.ToUInt64(await command.ExecuteScalarAsync()));

            await using var context = new ClickhouseDbContext(
                host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<ClickhouseOptions>>());
            context.RequestLogs.Add(new RequestLog
            {
                RequestTime = DateTime.UtcNow,
                Model = "consumer-model",
                UserId = "service-owner",
                ApiKeyId = 17,
                ReportedConsumerId = "consumer-a"
            });
            await context.SaveChangesAsync();
            command.CommandText = $"SELECT concat(UserId, ':', toString(ApiKeyId), ':', ReportedConsumerId) FROM `{database}`.ModelRequests WHERE Model = 'consumer-model'";
            Assert.AreEqual("service-owner:17:consumer-a", await command.ExecuteScalarAsync());
        }
        finally
        {
            command.CommandText = $"DROP DATABASE IF EXISTS `{database}` SYNC";
            await command.ExecuteNonQueryAsync();
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

        Assert.AreEqual(1, TestStartup.ClickhouseStub.SaveChangesCalls);
    }
}
