using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Aiursoft.DbTools;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Services;
using Aiursoft.OllamaGateway.Services.Clickhouse;
using Microsoft.EntityFrameworkCore;
using static Aiursoft.WebTools.Extends;
using Moq;

namespace Aiursoft.OllamaGateway.Tests.IntegrationTests;

/// <summary>
/// Integration tests covering OpenAI-compatible backend provider support:
///   A. Ollama upstream → OpenAI backend  (format translation)
///   B. OpenAI upstream → OpenAI backend  (direct passthrough)
/// </summary>
[TestClass]
public class OpenAIBackendProviderTests : TestBase
{
    private const string TestApiKey = "openai-backend-test-key";
    private const string ChatModelName = "gpt-virtual:latest";
    private const string EmbedModelName = "embed-virtual:latest";
    private const string PhysicalModelName = "gpt-4o-mini";
    private const string PhysicalEmbedModel = "text-embedding-3-small";

    [TestInitialize]
    public override async Task CreateServer()
    {
        TestStartup.MockClickhouse.Reset();
        TestStartup.MockClickhouse.Setup(c => c.Enabled).Returns(false);

        TestStartup.MockOllamaService.Reset();
        TestStartup.MockOllamaService
            .Setup(s => s.GetOpenAIAvailableModelsAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(new List<string> { PhysicalModelName, PhysicalEmbedModel });

        MockUpstreamState.Reset();

        Server = await AppAsync<TestStartup>([], port: Port);
        await Server.UpdateDbAsync<TemplateDbContext>();
        await Server.SeedAsync();
        await Server.StartAsync();

        using var scope = Server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<GlobalSettingsService>();
        await settings.UpdateSettingAsync(Configuration.SettingsMap.AllowAnonymousApiCall, "True");

        var provider = new OllamaProvider
        {
            Name = "OpenAI Backend",
            BaseUrl = "https://api.openai.test",
            BearerToken = "sk-test-token",
            ProviderType = ProviderType.OpenAI
        };
        db.OllamaProviders.Add(provider);
        await db.SaveChangesAsync();

        var chatModel = new VirtualModel { Name = ChatModelName, Type = ModelType.Chat };
        chatModel.VirtualModelBackends.Add(new VirtualModelBackend
        {
            ProviderId = provider.Id,
            UnderlyingModelName = PhysicalModelName,
            Enabled = true,
            IsHealthy = true
        });
        db.VirtualModels.Add(chatModel);

        var embedModel = new VirtualModel { Name = EmbedModelName, Type = ModelType.Embedding };
        embedModel.VirtualModelBackends.Add(new VirtualModelBackend
        {
            ProviderId = provider.Id,
            UnderlyingModelName = PhysicalEmbedModel,
            Enabled = true,
            IsHealthy = true
        });
        db.VirtualModels.Add(embedModel);
        await db.SaveChangesAsync();

        var user = await db.Users.FirstAsync();
        db.ApiKeys.Add(new ApiKey { Name = "OpenAI Backend Test Key", Key = TestApiKey, UserId = user.Id });
        await db.SaveChangesAsync();
    }

    private HttpRequestMessage AuthedPost(string url, string jsonBody)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestApiKey);
        return request;
    }

    // ========================================================================
    // A. Ollama upstream → OpenAI backend (format translation)
    // ========================================================================

    [TestMethod]
    public async Task OllamaToOpenAIBackend_NonStreaming_ForwardsToV1Chat()
    {
        MockUpstreamState.Handler = (_, _) =>
        {
            const string body =
                """{"id":"cmpl-1","object":"chat.completion","model":"gpt-4o-mini","choices":[{"message":{"role":"assistant","content":"Hello from OpenAI!"},"finish_reason":"stop","index":0}],"usage":{"prompt_tokens":10,"completion_tokens":4,"total_tokens":14}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        };

        var payload = $$"""{"model":"{{ChatModelName}}","messages":[{"role":"user","content":"Hi"}],"stream":false}""";
        var response = await Http.SendAsync(AuthedPost("/api/chat", payload));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(
            MockUpstreamState.LastRequest?.RequestUri?.PathAndQuery.Contains("/v1/chat/completions") ?? false,
            "Upstream should be called at /v1/chat/completions for OpenAI backend");

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(content);
        Assert.IsNotNull(json);
        Assert.AreEqual("Hello from OpenAI!", json["message"]?["content"]?.ToString());
        Assert.AreEqual(true, json["done"]?.GetValue<bool>());
        Assert.AreEqual(ChatModelName, json["model"]?.ToString(), "Response model must be masked to virtual name");
    }

    [TestMethod]
    public async Task OllamaToOpenAIBackend_ToolResultHistory_PreservesCallId()
    {
        MockUpstreamState.Handler = (_, _) =>
        {
            const string body =
                """{"id":"cmpl-tool","object":"chat.completion","model":"gpt-4o-mini","choices":[{"message":{"role":"assistant","content":"The current time is known."},"finish_reason":"stop","index":0}],"usage":{"prompt_tokens":20,"completion_tokens":6,"total_tokens":26}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        };

        var payload = $$$"""
        {
          "model":"{{{ChatModelName}}}",
          "messages":[
            {"role":"user","content":"What time is it?"},
            {"role":"assistant","content":"","tool_calls":[{
              "id":"call_timestamp_1",
              "function":{"name":"get_current_timestamp","arguments":{}}
            }]},
            {"role":"tool","tool_call_id":"call_timestamp_1","content":"{\"current_iso\":\"2026-08-17T12:34:45Z\"}"}
          ],
          "stream":false
        }
        """;

        var response = await Http.SendAsync(AuthedPost("/api/chat", payload));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsNotNull(MockUpstreamState.LastRequestBody);
        var messages = JsonNode.Parse(MockUpstreamState.LastRequestBody)?["messages"]?.AsArray();
        Assert.IsNotNull(messages);
        Assert.AreEqual(3, messages.Count);
        Assert.AreEqual("assistant", messages[1]?["role"]?.ToString());
        Assert.AreEqual("call_timestamp_1", messages[1]?["tool_calls"]?[0]?["id"]?.ToString());
        Assert.AreEqual("tool", messages[2]?["role"]?.ToString());
        Assert.AreEqual("call_timestamp_1", messages[2]?["tool_call_id"]?.ToString());
        Assert.AreEqual(
            "{\"current_iso\":\"2026-08-17T12:34:45Z\"}",
            messages[2]?["content"]?.ToString());
    }

    [TestMethod]
    public async Task OllamaToOpenAIBackend_NonStreaming_BearerTokenForwarded()
    {
        string? capturedAuth = null;
        MockUpstreamState.Handler = (req, _) =>
        {
            capturedAuth = req.Headers.Authorization?.Parameter;
            const string body =
                """{"id":"x","object":"chat.completion","choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        };

        var payload = $$"""{"model":"{{ChatModelName}}","messages":[{"role":"user","content":"Hi"}],"stream":false}""";
        await Http.SendAsync(AuthedPost("/api/chat", payload));

        Assert.AreEqual("sk-test-token", capturedAuth,
            "Bearer token from provider must be forwarded to the OpenAI upstream");
    }

    [TestMethod]
    public async Task OllamaToOpenAIBackend_NonStreaming_PhysicalModelNameSentUpstream()
    {
        MockUpstreamState.Handler = (_, _) =>
        {
            const string body =
                """{"id":"x","object":"chat.completion","choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        };

        var payload = $$"""{"model":"{{ChatModelName}}","messages":[{"role":"user","content":"Hi"}],"stream":false}""";
        await Http.SendAsync(AuthedPost("/api/chat", payload));

        Assert.IsNotNull(MockUpstreamState.LastRequestBody);
        var upstreamBody = JsonNode.Parse(MockUpstreamState.LastRequestBody);
        Assert.AreEqual(PhysicalModelName, upstreamBody?["model"]?.ToString(),
            "Physical model name must be forwarded to the OpenAI upstream");
    }

    [TestMethod]
    public async Task OllamaToOpenAIBackend_Streaming_ReturnsOllamaNDJSON()
    {
        MockUpstreamState.Handler = (_, _) =>
        {
            const string sse =
                "data: {\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"model\":\"gpt-4o-mini\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Hi\"},\"finish_reason\":null}]}\n\n" +
                "data: {\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"model\":\"gpt-4o-mini\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
                "data: {\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"model\":\"gpt-4o-mini\",\"choices\":[],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":1,\"total_tokens\":6}}\n\n" +
                "data: [DONE]\n\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            });
        };

        var payload = $$"""{"model":"{{ChatModelName}}","messages":[{"role":"user","content":"Hi"}],"stream":true}""";
        var logBuffer = Server!.Services.GetRequiredService<RequestLogBuffer>();
        logBuffer.Drain([]);
        var response = await Http.SendAsync(AuthedPost("/api/chat", payload));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        // Must be Ollama NDJSON (no SSE prefix)
        Assert.IsFalse(body.Contains("data: "), "Ollama NDJSON response must not have SSE 'data: ' prefix");
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.IsTrue(lines.Length >= 1, "Should produce at least one NDJSON line");

        var firstChunk = JsonNode.Parse(lines[0]);
        Assert.IsNotNull(firstChunk);
        Assert.AreEqual("Hi", firstChunk["message"]?["content"]?.ToString());
        Assert.AreEqual(ChatModelName, firstChunk["model"]?.ToString(), "Model must be masked in NDJSON chunks");

        var completed = lines
            .Select(line => JsonNode.Parse(line))
            .Where(item => item?["done"]?.GetValue<bool>() == true)
            .ToList();
        Assert.AreEqual(1, completed.Count, "Should produce exactly one terminal NDJSON line");
        Assert.AreEqual("Hi", completed[0]?["message"]?["content"]?.ToString());
        Assert.AreEqual(5L, completed[0]?["prompt_eval_count"]?.GetValue<long>());
        Assert.AreEqual(1L, completed[0]?["eval_count"]?.GetValue<long>());

        var logs = new List<RequestLog>();
        logBuffer.Drain(logs);
        Assert.AreEqual(1, logs.Count);
        Assert.IsTrue(logs[0].Success);
        Assert.AreEqual("Hi", logs[0].Answer);
        Assert.AreEqual(5, logs[0].PromptTokens);
        Assert.AreEqual(1, logs[0].CompletionTokens);
        Assert.AreEqual(6, logs[0].TotalTokens);

        var recent = Server.Services.GetRequiredService<ActiveRequestTracker>().GetRecentRequests();
        Assert.AreEqual(1, recent.Count);
        Assert.AreEqual("Completed", recent[0].Status);
        Assert.AreEqual("", recent[0].ErrorMessage);
        Assert.AreEqual("Hi", recent[0].Answer);
    }

    [TestMethod]
    public async Task OllamaToOpenAIBackend_Generate_Returns501()
    {
        var payload = $$"""{"model":"{{ChatModelName}}","prompt":"Hello","stream":false}""";
        var response = await Http.SendAsync(AuthedPost("/api/generate", payload));

        Assert.AreEqual(HttpStatusCode.NotImplemented, response.StatusCode,
            "/api/generate must return 501 for OpenAI-compatible backends");
    }

    [TestMethod]
    public async Task OllamaGenerate_RetrySkipsIncompatibleOpenAiBackend()
    {
        const string retryModelName = "generate-capability-retry:latest";
        const string primaryPhysicalModel = "llama-generate-primary";
        const string fallbackPhysicalModel = "llama-generate-fallback";
        int primaryProviderId;
        int incompatibleProviderId;
        int fallbackProviderId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var openAiProvider = await db.OllamaProviders
                .Where(provider => provider.Name == "OpenAI Backend")
                .OrderByDescending(provider => provider.Id)
                .FirstAsync();
            var primaryOllama = new OllamaProvider
            {
                Name = "Primary Generate Ollama",
                BaseUrl = "http://primary-generate-ollama.test:11434",
                ProviderType = ProviderType.Ollama
            };
            var fallbackOllama = new OllamaProvider
            {
                Name = "Fallback Generate Ollama",
                BaseUrl = "http://fallback-generate-ollama.test:11434",
                ProviderType = ProviderType.Ollama
            };
            db.OllamaProviders.AddRange(primaryOllama, fallbackOllama);
            await db.SaveChangesAsync();
            primaryProviderId = primaryOllama.Id;
            incompatibleProviderId = openAiProvider.Id;
            fallbackProviderId = fallbackOllama.Id;

            var virtualModel = new VirtualModel
            {
                Name = retryModelName,
                Type = ModelType.Chat,
                MaxRetries = 4
            };
            virtualModel.VirtualModelBackends.Add(new VirtualModelBackend
            {
                ProviderId = primaryOllama.Id,
                UnderlyingModelName = primaryPhysicalModel,
                Priority = 0,
                Enabled = true,
                IsHealthy = true
            });
            virtualModel.VirtualModelBackends.Add(new VirtualModelBackend
            {
                ProviderId = openAiProvider.Id,
                UnderlyingModelName = PhysicalModelName,
                Priority = 1,
                Enabled = true,
                IsHealthy = true
            });
            virtualModel.VirtualModelBackends.Add(new VirtualModelBackend
            {
                ProviderId = fallbackOllama.Id,
                UnderlyingModelName = fallbackPhysicalModel,
                Priority = 2,
                Enabled = true,
                IsHealthy = true
            });
            db.VirtualModels.Add(virtualModel);
            await db.SaveChangesAsync();
        }

        var attempts = new List<(string Host, string Path, string Body)>();
        MockUpstreamState.Handler = async (request, cancellationToken) =>
        {
            var body = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            attempts.Add((
                request.RequestUri?.Host ?? string.Empty,
                request.RequestUri?.AbsolutePath ?? string.Empty,
                body));

            if (request.RequestUri?.Host == "primary-generate-ollama.test")
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent(
                        """{"error":"temporary generate failure"}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"model":"llama-generate-fallback","response":"fallback worked","done":true,"prompt_eval_count":3,"eval_count":2}""",
                    Encoding.UTF8,
                    "application/json")
            };
        };

        var payload = $$"""{"model":"{{retryModelName}}","prompt":"retry generate","stream":false}""";
        var response = await Http.SendAsync(AuthedPost("/api/generate", payload));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(2, attempts.Count);
        Assert.AreEqual("primary-generate-ollama.test", attempts[0].Host);
        Assert.IsTrue(attempts.All(attempt => attempt.Path == "/api/generate"));
        Assert.IsFalse(attempts.Any(attempt => attempt.Host == "api.openai.test"),
            "An OpenAI-compatible backend must never be selected for /api/generate.");
        Assert.AreEqual("fallback-generate-ollama.test", attempts[1].Host);
        Assert.AreEqual(fallbackPhysicalModel, JsonNode.Parse(attempts[1].Body)?["model"]?.ToString());

        var responseBody = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(retryModelName, responseBody?["model"]?.ToString());
        Assert.AreEqual("fallback worked", responseBody?["response"]?.ToString());

        var usageCounter = Server!.Services.GetRequiredService<UsageCounter>();
        var (modelUsages, _) = usageCounter.SwapModelBuffers();
        Assert.AreEqual(1L, modelUsages[(primaryProviderId, primaryPhysicalModel)]);
        Assert.AreEqual(1L, modelUsages[(fallbackProviderId, fallbackPhysicalModel)]);
        Assert.IsFalse(modelUsages.ContainsKey((incompatibleProviderId, PhysicalModelName)),
            "A backend skipped by the capability planner must not be counted as an attempt.");
    }

    [TestMethod]
    public async Task OllamaToOpenAIBackend_Embed_ForwardsToV1Embeddings()
    {
        MockUpstreamState.Handler = (_, _) =>
        {
            const string body =
                """{"object":"list","data":[{"object":"embedding","embedding":[0.1,0.2,0.3],"index":0}],"model":"text-embedding-3-small","usage":{"prompt_tokens":3,"total_tokens":3}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        };

        var payload = $$"""{"model":"{{EmbedModelName}}","input":"hello world"}""";
        var response = await Http.SendAsync(AuthedPost("/api/embed", payload));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(
            MockUpstreamState.LastRequest?.RequestUri?.PathAndQuery.Contains("/v1/embeddings") ?? false,
            "Upstream should be called at /v1/embeddings for OpenAI embedding backend");

        // Response must be Ollama embed format: {"model":"...","embeddings":[[...]]}
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(content);
        Assert.IsNotNull(json);
        Assert.AreEqual(EmbedModelName, json["model"]?.ToString(), "Model must be masked to virtual name");
        var embeddings = json["embeddings"]?.AsArray();
        Assert.IsNotNull(embeddings, "Response must contain 'embeddings' array");
        Assert.AreEqual(1, embeddings.Count);
    }

    // ========================================================================
    // B. OpenAI upstream → OpenAI backend (direct passthrough)
    // ========================================================================

    [TestMethod]
    public async Task OpenAIToOpenAIBackend_NonStreaming_PassthroughWithMaskedModel()
    {
        MockUpstreamState.Handler = (_, _) =>
        {
            const string body =
                """{"id":"x","object":"chat.completion","model":"gpt-4o-mini","choices":[{"message":{"role":"assistant","content":"Passthrough!"},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":2,"total_tokens":7}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        };

        var payload = $$"""{"model":"{{ChatModelName}}","messages":[{"role":"user","content":"Hi"}],"stream":false}""";
        var response = await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(
            MockUpstreamState.LastRequest?.RequestUri?.PathAndQuery.Contains("/v1/chat/completions") ?? false,
            "Upstream should be called at /v1/chat/completions");

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(content);
        Assert.AreEqual(ChatModelName, json?["model"]?.ToString(), "Response model must be masked to virtual name");
        Assert.AreEqual("Passthrough!", json?["choices"]?[0]?["message"]?["content"]?.ToString());
    }

    [TestMethod]
    public async Task OpenAIToOpenAIBackend_NonStreaming_PhysicalModelSentUpstream()
    {
        MockUpstreamState.Handler = (_, _) =>
        {
            const string body =
                """{"id":"x","object":"chat.completion","model":"gpt-4o-mini","choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        };

        var payload = $$"""{"model":"{{ChatModelName}}","messages":[{"role":"user","content":"Hi"}],"stream":false}""";
        await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));

        Assert.IsNotNull(MockUpstreamState.LastRequestBody);
        var upstreamBody = JsonNode.Parse(MockUpstreamState.LastRequestBody);
        Assert.AreEqual(PhysicalModelName, upstreamBody?["model"]?.ToString(),
            "Physical model name must be used in the upstream request");
    }

    [TestMethod]
    public async Task OpenAIToOpenAIBackend_Streaming_PassthroughSSE()
    {
        MockUpstreamState.Handler = (_, _) =>
        {
            var sse =
                $"data: {{\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"model\":\"{PhysicalModelName}\",\"choices\":[{{\"index\":0,\"delta\":{{\"content\":\"Hello\"}},\"finish_reason\":null}}]}}\n\n" +
                $"data: {{\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"model\":\"{PhysicalModelName}\",\"choices\":[{{\"index\":0,\"delta\":{{}},\"finish_reason\":\"stop\"}}],\"usage\":{{\"prompt_tokens\":5,\"completion_tokens\":1,\"total_tokens\":6}}}}\n\n" +
                "data: [DONE]\n\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            });
        };

        var payload = $$"""{"model":"{{ChatModelName}}","messages":[{"role":"user","content":"Hi"}],"stream":true}""";
        var response = await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        // Response must remain SSE (passthrough)
        Assert.IsTrue(body.Contains("data: "), "Response should remain in SSE format for OpenAI upstream passthrough");
        Assert.IsTrue(body.Contains("data: [DONE]"), "SSE stream must end with [DONE]");

        // Model in first chunk should be masked to virtual name
        var firstDataLine = body.Split('\n').First(l => l.StartsWith("data: ") && l != "data: [DONE]");
        var firstChunk = JsonNode.Parse(firstDataLine["data: ".Length..]);
        Assert.AreEqual(ChatModelName, firstChunk?["model"]?.ToString(),
            "Model must be masked to virtual name in SSE chunks");
    }

    [TestMethod]
    public async Task OpenAIToOpenAIBackend_Embed_PassthroughWithMaskedModel()
    {
        MockUpstreamState.Handler = (_, _) =>
        {
            const string body =
                """{"object":"list","data":[{"object":"embedding","embedding":[0.4,0.5,0.6],"index":0}],"model":"text-embedding-3-small","usage":{"prompt_tokens":2,"total_tokens":2}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        };

        var payload = $$"""{"model":"{{EmbedModelName}}","input":"test phrase"}""";
        var response = await Http.SendAsync(AuthedPost("/v1/embeddings", payload));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(
            MockUpstreamState.LastRequest?.RequestUri?.PathAndQuery.Contains("/v1/embeddings") ?? false,
            "Upstream should be called at /v1/embeddings");

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(content);
        Assert.AreEqual(EmbedModelName, json?["model"]?.ToString(), "Response model must be masked to virtual name");
        Assert.IsNotNull(json?["data"]?[0]?["embedding"], "Embedding data must be preserved in passthrough");
    }

    [TestMethod]
    public async Task OpenAIEmbedding_RetryAcrossProviderDialects_ReencodesEveryAttempt()
    {
        const string ollamaPhysicalModel = "nomic-embed-text";
        const string retryModelName = "embed-retry-mixed:latest";
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var openAiProvider = await db.OllamaProviders
                .Where(provider => provider.Name == "OpenAI Backend")
                .OrderByDescending(provider => provider.Id)
                .FirstAsync();
            var ollamaProvider = new OllamaProvider
            {
                Name = "Primary Ollama Backend",
                BaseUrl = "http://primary-ollama.test:11434",
                ProviderType = ProviderType.Ollama
            };
            db.OllamaProviders.Add(ollamaProvider);
            await db.SaveChangesAsync();

            var virtualModel = new VirtualModel
            {
                Name = retryModelName,
                Type = ModelType.Embedding,
                MaxRetries = 4
            };
            virtualModel.VirtualModelBackends.Add(new VirtualModelBackend
            {
                ProviderId = ollamaProvider.Id,
                UnderlyingModelName = ollamaPhysicalModel,
                Priority = 0,
                Enabled = true,
                IsHealthy = true
            });
            virtualModel.VirtualModelBackends.Add(new VirtualModelBackend
            {
                ProviderId = openAiProvider.Id,
                UnderlyingModelName = PhysicalEmbedModel,
                Priority = 1,
                Enabled = true,
                IsHealthy = true
            });
            db.VirtualModels.Add(virtualModel);
            await db.SaveChangesAsync();
        }

        var attempts = new List<(string Path, string Body)>();
        MockUpstreamState.Handler = async (request, cancellationToken) =>
        {
            var body = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            attempts.Add((request.RequestUri?.AbsolutePath ?? string.Empty, body));

            if (request.RequestUri?.Host == "primary-ollama.test")
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("""{"error":"temporary Ollama failure"}""", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"object":"list","data":[{"object":"embedding","embedding":[0.7,0.8],"index":0}],"model":"text-embedding-3-small","usage":{"prompt_tokens":2,"total_tokens":2}}""",
                    Encoding.UTF8,
                    "application/json")
            };
        };

        var payload = $$"""{"model":"{{retryModelName}}","input":"retry me"}""";
        var response = await Http.SendAsync(AuthedPost("/v1/embeddings", payload));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(2, attempts.Count, "A single request should try the next eligible backend immediately.");
        Assert.AreEqual("/api/embed", attempts[0].Path);
        Assert.AreEqual(ollamaPhysicalModel, JsonNode.Parse(attempts[0].Body)?["model"]?.ToString());
        Assert.AreEqual("/v1/embeddings", attempts[1].Path);
        Assert.AreEqual(PhysicalEmbedModel, JsonNode.Parse(attempts[1].Body)?["model"]?.ToString());

        var responseBody = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(retryModelName, responseBody?["model"]?.ToString());
        Assert.IsNotNull(responseBody?["data"]?[0]?["embedding"]);
    }

    [TestMethod]
    public async Task OpenAIChat_RetryAcrossProviderDialects_ReencodesRequestAndResponse()
    {
        const string ollamaPhysicalModel = "llama-primary";
        const string retryModelName = "chat-retry-mixed:latest";
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var openAiProvider = await db.OllamaProviders
                .Where(provider => provider.Name == "OpenAI Backend")
                .OrderByDescending(provider => provider.Id)
                .FirstAsync();
            openAiProvider.SupportsOpenAiChatCompletions = false;
            openAiProvider.SupportsOpenAiResponses = true;
            var ollamaProvider = new OllamaProvider
            {
                Name = "Primary Ollama Chat",
                BaseUrl = "http://primary-chat-ollama.test:11434",
                ProviderType = ProviderType.Ollama
            };
            db.OllamaProviders.Add(ollamaProvider);
            await db.SaveChangesAsync();

            var virtualModel = new VirtualModel
            {
                Name = retryModelName,
                Type = ModelType.Chat,
                MaxRetries = 4
            };
            virtualModel.VirtualModelBackends.Add(new VirtualModelBackend
            {
                ProviderId = ollamaProvider.Id,
                UnderlyingModelName = ollamaPhysicalModel,
                Priority = 0,
                Enabled = true,
                IsHealthy = true
            });
            virtualModel.VirtualModelBackends.Add(new VirtualModelBackend
            {
                ProviderId = openAiProvider.Id,
                UnderlyingModelName = PhysicalModelName,
                Protocol = BackendProtocol.OpenAiResponses,
                Priority = 1,
                Enabled = true,
                IsHealthy = true
            });
            db.VirtualModels.Add(virtualModel);
            await db.SaveChangesAsync();
        }

        var attempts = new List<(string Path, string Body)>();
        MockUpstreamState.Handler = async (request, cancellationToken) =>
        {
            var body = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            attempts.Add((request.RequestUri?.AbsolutePath ?? string.Empty, body));

            if (request.RequestUri?.Host == "primary-chat-ollama.test")
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("""{"error":"temporary Ollama chat failure"}""", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"id":"resp_fallback","object":"response","created_at":1,"status":"completed","model":"gpt-4o-mini","output":[{"id":"msg_fallback","type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","text":"fallback worked","annotations":[]}]}],"usage":{"input_tokens":3,"output_tokens":2,"total_tokens":5}}""",
                    Encoding.UTF8,
                    "application/json")
            };
        };

        var payload = $$"""{"model":"{{retryModelName}}","messages":[{"role":"user","content":"retry chat"}],"stream":false}""";
        var response = await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(2, attempts.Count);
        Assert.AreEqual("/api/chat", attempts[0].Path);
        Assert.AreEqual(ollamaPhysicalModel, JsonNode.Parse(attempts[0].Body)?["model"]?.ToString());
        Assert.AreEqual("/v1/responses", attempts[1].Path);
        Assert.AreEqual(PhysicalModelName, JsonNode.Parse(attempts[1].Body)?["model"]?.ToString());

        var responseBody = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(retryModelName, responseBody?["model"]?.ToString());
        Assert.AreEqual("fallback worked", responseBody?["choices"]?[0]?["message"]?["content"]?.ToString());
        Assert.AreEqual(5, responseBody?["usage"]?["total_tokens"]?.GetValue<int>());
    }

    [TestMethod]
    public async Task OllamaChat_RetryAcrossProviderDialects_ReencodesRequestAndResponse()
    {
        const string retryModelName = "ollama-client-retry-mixed:latest";
        const string ollamaPhysicalModel = "llama-fallback";
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var openAiProvider = await db.OllamaProviders
                .Where(provider => provider.Name == "OpenAI Backend")
                .OrderByDescending(provider => provider.Id)
                .FirstAsync();
            var ollamaProvider = new OllamaProvider
            {
                Name = "Fallback Ollama Chat",
                BaseUrl = "http://fallback-chat-ollama.test:11434",
                ProviderType = ProviderType.Ollama
            };
            db.OllamaProviders.Add(ollamaProvider);
            await db.SaveChangesAsync();

            var virtualModel = new VirtualModel
            {
                Name = retryModelName,
                Type = ModelType.Chat,
                MaxRetries = 4
            };
            virtualModel.VirtualModelBackends.Add(new VirtualModelBackend
            {
                ProviderId = openAiProvider.Id,
                UnderlyingModelName = PhysicalModelName,
                Priority = 0,
                Enabled = true,
                IsHealthy = true
            });
            virtualModel.VirtualModelBackends.Add(new VirtualModelBackend
            {
                ProviderId = ollamaProvider.Id,
                UnderlyingModelName = ollamaPhysicalModel,
                Priority = 1,
                Enabled = true,
                IsHealthy = true
            });
            db.VirtualModels.Add(virtualModel);
            await db.SaveChangesAsync();
        }

        var attempts = new List<(string Path, string Body)>();
        MockUpstreamState.Handler = async (request, cancellationToken) =>
        {
            var body = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            attempts.Add((request.RequestUri?.AbsolutePath ?? string.Empty, body));

            if (request.RequestUri?.Host == "api.openai.test")
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("""{"error":"temporary OpenAI chat failure"}""", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"model":"llama-fallback","message":{"role":"assistant","content":"ollama fallback worked"},"done":true,"prompt_eval_count":4,"eval_count":3}""",
                    Encoding.UTF8,
                    "application/json")
            };
        };

        var payload = $$"""{"model":"{{retryModelName}}","messages":[{"role":"user","content":"retry from Ollama client"}],"stream":false}""";
        var response = await Http.SendAsync(AuthedPost("/api/chat", payload));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(2, attempts.Count);
        Assert.AreEqual("/v1/chat/completions", attempts[0].Path);
        Assert.AreEqual(PhysicalModelName, JsonNode.Parse(attempts[0].Body)?["model"]?.ToString());
        Assert.AreEqual("/api/chat", attempts[1].Path);
        Assert.AreEqual(ollamaPhysicalModel, JsonNode.Parse(attempts[1].Body)?["model"]?.ToString());

        var responseBody = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(retryModelName, responseBody?["model"]?.ToString());
        Assert.AreEqual("ollama fallback worked", responseBody?["message"]?["content"]?.ToString());
        Assert.AreEqual(4, responseBody?["prompt_eval_count"]?.GetValue<int>());
        Assert.AreEqual(3, responseBody?["eval_count"]?.GetValue<int>());
    }

    [TestMethod]
    public async Task AnthropicChat_RetryAcrossProviderDialects_UsesEventIrForResponse()
    {
        const string retryModelName = "anthropic-retry-mixed:latest";
        const string ollamaPhysicalModel = "llama-anthropic-primary";
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var openAiProvider = await db.OllamaProviders
                .Where(provider => provider.Name == "OpenAI Backend")
                .OrderByDescending(provider => provider.Id)
                .FirstAsync();
            var ollamaProvider = new OllamaProvider
            {
                Name = "Primary Ollama for Anthropic",
                BaseUrl = "http://anthropic-primary-ollama.test:11434",
                ProviderType = ProviderType.Ollama
            };
            db.OllamaProviders.Add(ollamaProvider);
            await db.SaveChangesAsync();

            var virtualModel = new VirtualModel
            {
                Name = retryModelName,
                Type = ModelType.Chat,
                MaxRetries = 4
            };
            virtualModel.VirtualModelBackends.Add(new VirtualModelBackend
            {
                ProviderId = ollamaProvider.Id,
                UnderlyingModelName = ollamaPhysicalModel,
                Priority = 0,
                Enabled = true,
                IsHealthy = true
            });
            virtualModel.VirtualModelBackends.Add(new VirtualModelBackend
            {
                ProviderId = openAiProvider.Id,
                UnderlyingModelName = PhysicalModelName,
                Priority = 1,
                Enabled = true,
                IsHealthy = true
            });
            db.VirtualModels.Add(virtualModel);
            await db.SaveChangesAsync();
        }

        var attempts = new List<string>();
        MockUpstreamState.Handler = (request, _) =>
        {
            attempts.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
            if (request.RequestUri?.Host == "anthropic-primary-ollama.test")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("""{"error":"temporary Ollama failure"}""", Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"id":"fallback","object":"chat.completion","model":"gpt-4o-mini","choices":[{"index":0,"message":{"role":"assistant","reasoning_content":"checked","content":"I will call a tool","tool_calls":[{"id":"call_7","type":"function","function":{"name":"lookup","arguments":"{\"q\":\"x\"}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":6,"completion_tokens":4,"total_tokens":10}}""",
                    Encoding.UTF8,
                    "application/json")
            });
        };

        var payload = $$"""{"model":"{{retryModelName}}","max_tokens":100,"messages":[{"role":"user","content":"use a tool"}],"stream":false}""";
        var response = await Http.SendAsync(AuthedPost("/v1/messages", payload));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        CollectionAssert.AreEqual(
            new[] { "/api/chat", "/v1/chat/completions" },
            attempts);
        var responseBody = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(retryModelName, responseBody?["model"]?.ToString());
        Assert.AreEqual("tool_use", responseBody?["stop_reason"]?.ToString());
        Assert.AreEqual("thinking", responseBody?["content"]?[0]?["type"]?.ToString());
        Assert.AreEqual("I will call a tool", responseBody?["content"]?[1]?["text"]?.ToString());
        Assert.AreEqual("lookup", responseBody?["content"]?[2]?["name"]?.ToString());
        Assert.AreEqual("x", responseBody?["content"]?[2]?["input"]?["q"]?.ToString());
        Assert.AreEqual(6, responseBody?["usage"]?["input_tokens"]?.GetValue<int>());
        Assert.AreEqual(4, responseBody?["usage"]?["output_tokens"]?.GetValue<int>());
    }

    [TestMethod]
    public async Task AnthropicClaudeCodeControls_FallBackToOpenAiChatBackend()
    {
        MockUpstreamState.Handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"id":"claude-fallback","object":"chat.completion","model":"gpt-4o-mini","choices":[{"index":0,"message":{"role":"assistant","content":"Claude Code works"},"finish_reason":"stop"}],"usage":{"prompt_tokens":8,"completion_tokens":4,"total_tokens":12}}""",
                Encoding.UTF8,
                "application/json")
        });

        var payload = $$"""
        {
          "model":"{{ChatModelName}}",
          "max_tokens":1024,
          "messages":[{"role":"user","content":"hello"}],
          "stream":false,
          "top_k":40,
          "thinking":{"type":"adaptive"},
          "metadata":{"user_id":"session"},
          "stop_sequences":["stop"],
          "service_tier":"auto"
        }
        """;

        var response = await Http.SendAsync(AuthedPost("/v1/messages?beta=true", payload));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("/v1/chat/completions", MockUpstreamState.LastRequest?.RequestUri?.AbsolutePath);
        var upstream = JsonNode.Parse(MockUpstreamState.LastRequestBody!);
        Assert.AreEqual(PhysicalModelName, upstream?["model"]?.ToString());
        Assert.AreEqual(true, upstream?["chat_template_kwargs"]?["enable_thinking"]?.GetValue<bool>());
        Assert.IsNull(upstream?["metadata"]);
        Assert.IsNull(upstream?["service_tier"]);

        var result = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("Claude Code works", result?["content"]?[0]?["text"]?.ToString());
    }

    [TestMethod]
    public async Task OpenAiSoftPassthrough_PrefersMatchingBackendOverHigherPriorityTranslation()
    {
        const string modelName = "openai-soft-preference:latest";
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var openAiProvider = await db.OllamaProviders
                .Where(provider => provider.Name == "OpenAI Backend")
                .OrderByDescending(provider => provider.Id)
                .FirstAsync();
            var ollamaProvider = new OllamaProvider
            {
                Name = "Higher priority Ollama",
                BaseUrl = "http://soft-preference-ollama.test:11434",
                ProviderType = ProviderType.Ollama
            };
            db.OllamaProviders.Add(ollamaProvider);
            await db.SaveChangesAsync();

            var virtualModel = new VirtualModel
            {
                Name = modelName,
                Type = ModelType.Chat,
                SelectionStrategy = SelectionStrategy.PriorityFallback
            };
            virtualModel.VirtualModelBackends.Add(new VirtualModelBackend
            {
                ProviderId = ollamaProvider.Id,
                UnderlyingModelName = "llama-primary",
                Priority = 0,
                Enabled = true,
                IsHealthy = true
            });
            virtualModel.VirtualModelBackends.Add(new VirtualModelBackend
            {
                ProviderId = openAiProvider.Id,
                UnderlyingModelName = PhysicalModelName,
                Priority = 1,
                Enabled = true,
                IsHealthy = true
            });
            db.VirtualModels.Add(virtualModel);
            await db.SaveChangesAsync();
        }

        MockUpstreamState.Handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"id":"soft","object":"chat.completion","model":"gpt-4o-mini","choices":[{"index":0,"message":{"role":"assistant","content":"preserved"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":1,"total_tokens":4}}""",
                Encoding.UTF8,
                "application/json")
        });

        var payload = $$$"""{"model":"{{{modelName}}}","messages":[{"role":"user","content":"hi"}],"vendor_extension":{"keep":true}}""";
        var response = await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("/v1/chat/completions", MockUpstreamState.LastRequest?.RequestUri?.AbsolutePath);
        Assert.AreEqual(true,
            JsonNode.Parse(MockUpstreamState.LastRequestBody!)?["vendor_extension"]?["keep"]?.GetValue<bool>());
    }

    [TestMethod]
    public async Task AnthropicToOpenAiBackend_ConvertsBase64AndUrlImages()
    {
        MockUpstreamState.Handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"id":"vision","object":"chat.completion","model":"gpt-4o-mini","choices":[{"index":0,"message":{"role":"assistant","content":"I see both images."},"finish_reason":"stop"}],"usage":{"prompt_tokens":8,"completion_tokens":4,"total_tokens":12}}""",
                Encoding.UTF8,
                "application/json")
        });

        var payload = $$$"""
        {
          "model":"{{{ChatModelName}}}","max_tokens":100,"messages":[{"role":"user","content":[
            {"type":"text","text":"describe"},
            {"type":"image","source":{"type":"base64","media_type":"image/jpeg","data":"AQID"}},
            {"type":"image","source":{"type":"url","url":"https://images.example.test/cat.png"}}
          ]}]
        }
        """;
        var response = await Http.SendAsync(AuthedPost("/v1/messages", payload));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var upstreamBody = JsonNode.Parse(MockUpstreamState.LastRequestBody!)!;
        var content = upstreamBody["messages"]?[0]?["content"];
        Assert.AreEqual("describe", content?[0]?["text"]?.ToString());
        Assert.AreEqual("data:image/jpeg;base64,AQID", content?[1]?["image_url"]?["url"]?.ToString());
        Assert.AreEqual("https://images.example.test/cat.png", content?[2]?["image_url"]?["url"]?.ToString());
    }

    // ========================================================================
    // C. Thinking injection: OpenAI provider paths
    // ========================================================================

    [TestMethod]
    public async Task OpenAIUpstreamToOpenAIBackend_VMThinkingTrue_InjectsChatTemplateKwargs()
    {
        // Path ①: OpenAI client → OpenAI backend, VM.Thinking = true
        var capturedRequestBody = string.Empty;
        using (var scope = Server!.Services.CreateScope())
        {
            Assert.IsNotNull(scope);
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var vm = await db.VirtualModels.FirstAsync(m => m.Name == ChatModelName);
            vm.Thinking = true;
            await db.SaveChangesAsync();
        }

        MockUpstreamState.Handler = (_, _) =>
        {
            capturedRequestBody = MockUpstreamState.LastRequestBody ?? string.Empty;
            const string body =
                """{"id":"t1","object":"chat.completion","choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        };

        var payload = $$"""{"model":"{{ChatModelName}}","messages":[{"role":"user","content":"Hi"}],"stream":false}""";
        await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));

        var upstreamBody = JsonNode.Parse(capturedRequestBody);
        Assert.IsNotNull(upstreamBody, "Upstream request body must be captured");
        Assert.AreEqual(true, upstreamBody["chat_template_kwargs"]?["enable_thinking"]?.GetValue<bool>(),
            "chat_template_kwargs.enable_thinking must be true when VM.Thinking = true");
    }

    [TestMethod]
    public async Task OpenAIUpstreamToOpenAIBackend_VMThinkingFalse_InjectsChatTemplateKwargs()
    {
        // Path ①: OpenAI client → OpenAI backend, VM.Thinking = false
        var capturedRequestBody = string.Empty;
        using (var scope = Server!.Services.CreateScope())
        {
            Assert.IsNotNull(scope);
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var vm = await db.VirtualModels.FirstAsync(m => m.Name == ChatModelName);
            vm.Thinking = false;
            await db.SaveChangesAsync();
        }

        MockUpstreamState.Handler = (_, _) =>
        {
            capturedRequestBody = MockUpstreamState.LastRequestBody ?? string.Empty;
            const string body =
                """{"id":"t2","object":"chat.completion","choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        };

        var payload = $$"""{"model":"{{ChatModelName}}","messages":[{"role":"user","content":"Hi"}],"stream":false}""";
        await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));

        var upstreamBody = JsonNode.Parse(capturedRequestBody);
        Assert.IsNotNull(upstreamBody);
        Assert.AreEqual(false, upstreamBody["chat_template_kwargs"]?["enable_thinking"]?.GetValue<bool>(),
            "chat_template_kwargs.enable_thinking must be false when VM.Thinking = false");
    }

    [TestMethod]
    public async Task OpenAIUpstreamToOpenAIBackend_VMThinkingNull_PassesThroughClientValue()
    {
        // Path ①: OpenAI client → OpenAI backend, VM.Thinking = null → client value passes through
        var capturedRequestBody = string.Empty;
        using (var scope = Server!.Services.CreateScope())
        {
            Assert.IsNotNull(scope);
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var vm = await db.VirtualModels.FirstAsync(m => m.Name == ChatModelName);
            vm.Thinking = null;
            await db.SaveChangesAsync();
        }

        MockUpstreamState.Handler = (_, _) =>
        {
            capturedRequestBody = MockUpstreamState.LastRequestBody ?? string.Empty;
            const string body =
                """{"id":"t3","object":"chat.completion","choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        };

        var payload = $$$"""{"model":"{{{ChatModelName}}}","messages":[{"role":"user","content":"Hi"}],"stream":false,"chat_template_kwargs":{"enable_thinking":true}}""";
        await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));

        var upstreamBody = JsonNode.Parse(capturedRequestBody);
        Assert.IsNotNull(upstreamBody);
        Assert.AreEqual(true, upstreamBody["chat_template_kwargs"]?["enable_thinking"]?.GetValue<bool>(),
            "When VM.Thinking is null, client-supplied chat_template_kwargs must pass through unchanged");
    }

    [TestMethod]
    public async Task OpenAIUpstreamToOpenAIBackend_VMThinkingOverridesClientValue()
    {
        // Path ①: VM.Thinking = false must override client's enable_thinking = true
        var capturedRequestBody = string.Empty;
        using (var scope = Server!.Services.CreateScope())
        {
            Assert.IsNotNull(scope);
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var vm = await db.VirtualModels.FirstAsync(m => m.Name == ChatModelName);
            vm.Thinking = false;
            await db.SaveChangesAsync();
        }

        MockUpstreamState.Handler = (_, _) =>
        {
            capturedRequestBody = MockUpstreamState.LastRequestBody ?? string.Empty;
            const string body =
                """{"id":"t4","object":"chat.completion","choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        };

        var payload = $$$"""{"model":"{{{ChatModelName}}}","messages":[{"role":"user","content":"Hi"}],"stream":false,"chat_template_kwargs":{"enable_thinking":true}}""";
        await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));

        var upstreamBody = JsonNode.Parse(capturedRequestBody);
        Assert.IsNotNull(upstreamBody);
        Assert.AreEqual(false, upstreamBody["chat_template_kwargs"]?["enable_thinking"]?.GetValue<bool>(),
            "VM.Thinking = false must override client-supplied enable_thinking = true");
    }

    [TestMethod]
    public async Task OllamaUpstreamToOpenAIBackend_VMThinkingTrue_InjectsChatTemplateKwargs()
    {
        // Path ②: Ollama client → OpenAI backend, VM.Thinking = true
        var capturedRequestBody = string.Empty;
        using (var scope = Server!.Services.CreateScope())
        {
            Assert.IsNotNull(scope);
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var vm = await db.VirtualModels.FirstAsync(m => m.Name == ChatModelName);
            vm.Thinking = true;
            await db.SaveChangesAsync();
        }

        MockUpstreamState.Handler = (_, _) =>
        {
            capturedRequestBody = MockUpstreamState.LastRequestBody ?? string.Empty;
            const string body =
                """{"id":"t5","object":"chat.completion","choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        };

        var payload = $$"""{"model":"{{ChatModelName}}","messages":[{"role":"user","content":"Hi"}],"stream":false}""";
        await Http.SendAsync(AuthedPost("/api/chat", payload));

        Assert.IsTrue(
            MockUpstreamState.LastRequest?.RequestUri?.PathAndQuery.Contains("/v1/chat/completions") ?? false,
            "Upstream should call /v1/chat/completions for OpenAI backend");

        var upstreamBody = JsonNode.Parse(capturedRequestBody);
        Assert.IsNotNull(upstreamBody);
        Assert.AreEqual(true, upstreamBody["chat_template_kwargs"]?["enable_thinking"]?.GetValue<bool>(),
            "chat_template_kwargs.enable_thinking must be true when VM.Thinking = true (Ollama→OpenAI)");
    }

    // ========================================================================
    // D. Multimodal image translation: Ollama images → OpenAI content array
    // ========================================================================

    [TestMethod]
    public async Task OllamaToOpenAIBackend_WithImages_ConvertsToMultimodalContent()
    {
        // Ollama sends images as a separate array; the gateway must convert them
        // to OpenAI multimodal content parts (text + image_url).
        var capturedRequestBody = string.Empty;
        MockUpstreamState.Handler = (_, _) =>
        {
            capturedRequestBody = MockUpstreamState.LastRequestBody ?? string.Empty;
            const string body =
                """{"id":"img1","object":"chat.completion","choices":[{"message":{"role":"assistant","content":"I see a tiny 1x1 image!"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        };

        var testImageBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8/5+hHgAHggJ/PchI7wAAAABJRU5ErkJggg==";
        var payload = $$"""{"model":"{{ChatModelName}}","messages":[{"role":"user","content":"What is in this image?","images":["{{testImageBase64}}"]}],"stream":false}""";
        await Http.SendAsync(AuthedPost("/api/chat", payload));

        Assert.IsTrue(
            MockUpstreamState.LastRequest?.RequestUri?.PathAndQuery.Contains("/v1/chat/completions") ?? false,
            "Upstream should call /v1/chat/completions for OpenAI backend");

        var upstreamBody = JsonNode.Parse(capturedRequestBody);
        Assert.IsNotNull(upstreamBody);

        var messages = upstreamBody["messages"]?.AsArray();
        Assert.IsNotNull(messages);
        Assert.AreEqual(1, messages.Count);

        var content = messages[0]!["content"];
        Assert.IsNotNull(content);
        Assert.IsTrue(content is JsonArray,
            "When images are present, content must be a multimodal array, not a plain string");

        var contentParts = content.AsArray();
        Assert.AreEqual(2, contentParts.Count, "Should have 2 parts: text + image_url");

        // Part 0: text
        Assert.AreEqual("text", contentParts[0]!["type"]?.ToString());
        Assert.AreEqual("What is in this image?", contentParts[0]!["text"]?.ToString());

        // Part 1: image_url
        Assert.AreEqual("image_url", contentParts[1]!["type"]?.ToString());
        var imageUrl = contentParts[1]!["image_url"]?["url"]?.ToString();
        Assert.IsNotNull(imageUrl);
        StringAssert.Contains(imageUrl, "data:image/png;base64,",
            "Image URL must be prefixed with data URI scheme");
        StringAssert.Contains(imageUrl, testImageBase64,
            "Image URL must contain the base64 payload");
    }

    [TestMethod]
    public async Task OllamaToOpenAIBackend_WithoutImages_StillSendsPlainString()
    {
        // Regression: messages without images must still use plain string content (not multimodal array)
        var capturedRequestBody = string.Empty;
        MockUpstreamState.Handler = (_, _) =>
        {
            capturedRequestBody = MockUpstreamState.LastRequestBody ?? string.Empty;
            const string body =
                """{"id":"noimg","object":"chat.completion","choices":[{"message":{"role":"assistant","content":"Hello!"},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        };

        var payload = $$"""{"model":"{{ChatModelName}}","messages":[{"role":"user","content":"Hi"}],"stream":false}""";
        await Http.SendAsync(AuthedPost("/api/chat", payload));

        var upstreamBody = JsonNode.Parse(capturedRequestBody);
        Assert.IsNotNull(upstreamBody);

        var messages = upstreamBody["messages"]?.AsArray();
        Assert.IsNotNull(messages);
        var content = messages[0]!["content"];
        Assert.IsNotNull(content);
        Assert.IsTrue(content is not JsonArray,
            "Without images, content must remain a plain string, not a multimodal array (regression check)");
        Assert.AreEqual("Hi", content.ToString());
    }

    // ========================================================================
    // E. SSE passthrough fidelity — Regression tests for ReplaceModelField
    //    (string-based model replacement vs. JsonNode parse–re-serialize)
    // ========================================================================

    [TestMethod]
    public async Task OpenAIToOpenAIBackend_Streaming_PreservesJsonFieldOrder()
    {
        // Regression: the old JsonNode.Parse → modify → ToJsonString() round-trip
        // reordered JSON fields (model moved to the end). The ReplaceModelField
        // string-based approach must preserve exact upstream field order.
        MockUpstreamState.Handler = (_, _) =>
        {
            // Deliberately put "model" as the FIRST field — this is the natural
            // OpenAI API order.  After the old round-trip it would move to the end.
            var sse =
                "data: {\"model\":\"gpt-4o-mini\",\"id\":\"chatcmpl-001\",\"object\":\"chat.completion.chunk\",\"created\":1720000000,\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hi\"},\"finish_reason\":null}]}\n\n" +
                "data: [DONE]\n\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            });
        };

        var payload = $$"""{"model":"{{ChatModelName}}","messages":[{"role":"user","content":"Hi"}],"stream":true}""";
        var response = await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        // Find the first data line and check field order
        var firstDataLine = body.Split('\n')
            .First(l => l.StartsWith("data: ") && l != "data: [DONE]");
        var jsonPayload = firstDataLine["data: ".Length..];

        // "model" must still be the FIRST field (preserving upstream order)
        var modelFieldIndex = jsonPayload.IndexOf("\"model\"", StringComparison.Ordinal);
        var idFieldIndex = jsonPayload.IndexOf("\"id\"", StringComparison.Ordinal);
        Assert.IsTrue(modelFieldIndex < idFieldIndex,
            $"Model field should appear BEFORE id field (preserving upstream order).\nGot: {jsonPayload}");
    }

    [TestMethod]
    public async Task OpenAIToOpenAIBackend_Streaming_PreservesNumberPrecision()
    {
        // Regression: JsonNode stores numbers as double, which can lose precision
        // for large integers (e.g. 64-bit timestamps). String-based replacement
        // must preserve the exact numeric representation.
        MockUpstreamState.Handler = (_, _) =>
        {
            // "created" is a Unix timestamp that should survive round-trip exactly
            var sse =
                "data: {\"id\":\"x\",\"object\":\"chat.completion.chunk\",\"created\":1735689600000,\"model\":\"gpt-4o-mini\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
                "data: [DONE]\n\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            });
        };

        var payload = $$"""{"model":"{{ChatModelName}}","messages":[{"role":"user","content":"Hi"}],"stream":true}""";
        var response = await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        var firstDataLine = body.Split('\n')
            .First(l => l.StartsWith("data: ") && l != "data: [DONE]");
        // The timestamp must appear exactly as provided, not as scientific notation
        Assert.IsTrue(firstDataLine.Contains("1735689600000"),
            $"Large integer timestamp must survive passthrough intact.\nGot: {firstDataLine}");
    }

    [TestMethod]
    public async Task OpenAIToOpenAIBackend_Streaming_PreservesJsonNullValues()
    {
        // Regression: the default JsonSerializerOptions may drop null values
        // depending on configuration. String-based replacement must preserve them.
        MockUpstreamState.Handler = (_, _) =>
        {
            var sse =
                "data: {\"id\":\"x\",\"object\":\"chat.completion.chunk\",\"model\":\"gpt-4o-mini\",\"choices\":[{\"index\":0,\"delta\":{\"content\":null},\"logprobs\":null,\"finish_reason\":null}]}\n\n" +
                "data: [DONE]\n\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            });
        };

        var payload = $$"""{"model":"{{ChatModelName}}","messages":[{"role":"user","content":"Hi"}],"stream":true}""";
        var response = await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        // Verify null values are preserved
        Assert.IsTrue(body.Contains("\"content\":null"), "null JSON values must be preserved");
        Assert.IsTrue(body.Contains("\"logprobs\":null"), "null JSON values must be preserved");
        Assert.IsTrue(body.Contains("\"finish_reason\":null"), "null JSON values must be preserved");
    }

    [TestMethod]
    public async Task OpenAIToOpenAIBackend_Streaming_PreservesUnicodeInContent()
    {
        // Regression: System.Text.Json JavaScriptEncoder escapes certain characters
        // (like '+', '<', '>', '&') as \\uXXXX by default. String-based passthrough
        // must keep the original encoding.
        MockUpstreamState.Handler = (_, _) =>
        {
            var sse =
                "data: {\"id\":\"x\",\"object\":\"chat.completion.chunk\",\"model\":\"gpt-4o-mini\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hello \\u003cworld\\u003e & \\u0026 friends\"},\"finish_reason\":null}]}\n\n" +
                "data: [DONE]\n\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            });
        };

        var payload = $$"""{"model":"{{ChatModelName}}","messages":[{"role":"user","content":"Hi"}],"stream":true}""";
        var response = await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        // The exact \\u003c / \\u0026 sequences from upstream MUST survive.
        // The old JsonNode→ToJsonString path would re-encode these differently.
        Assert.IsTrue(body.Contains("\\u003c"), "Unicode escape \\u003c must be preserved as-is");
        Assert.IsTrue(body.Contains("\\u0026"), "Unicode escape \\u0026 must be preserved as-is");
    }

    [TestMethod]
    public async Task OpenAIToOpenAIBackend_Streaming_OnlyModelFieldChanged()
    {
        // The replaced JSON line must be IDENTICAL to the original except for the
        // quoted value of the "model" field.  Everything else — whitespace, colons,
        // field order — must remain byte-for-byte identical.
        var originalChunkJson = (string?)null;
        MockUpstreamState.Handler = (_, _) =>
        {
            originalChunkJson =
                "{\"id\":\"chatcmpl-002\",\"object\":\"chat.completion.chunk\",\"created\":1720000000,\"model\":\"gpt-4o-mini\",\"system_fingerprint\":\"fp_abc123\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Hello\"},\"finish_reason\":null}]}";
            var sse = $"data: {originalChunkJson}\n\ndata: [DONE]\n\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            });
        };

        var payload = $$"""{"model":"{{ChatModelName}}","messages":[{"role":"user","content":"Hi"}],"stream":true}""";
        var response = await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.IsNotNull(originalChunkJson);

        var firstDataLine = body.Split('\n')
            .First(l => l.StartsWith("data: ") && l != "data: [DONE]");
        var outputJson = firstDataLine["data: ".Length..];

        // Manual diff: only the model name should differ
        var expectedJson = originalChunkJson.Replace("gpt-4o-mini", ChatModelName);
        Assert.AreEqual(expectedJson, outputJson,
            $"Output JSON must be byte-identical to input except for the model field value.\n" +
            $"Expected: {expectedJson}\nActual:   {outputJson}");
    }

    [TestMethod]
    public async Task OpenAIToOpenAIBackend_Streaming_HandlesDataWithoutSpace()
    {
        // Regression: some OpenAI-compatible backends omit the space after "data:"
        // (i.e. "data:{" instead of "data: {"). The gateway must handle both.
        MockUpstreamState.Handler = (_, _) =>
        {
            // No space after "data:" — a common variation in non-OpenAI SSE servers
            var sse =
                "data:{\"model\":\"gpt-4o-mini\",\"id\":\"x\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"NoSpace\"},\"finish_reason\":\"stop\"}]}\n\n" +
                "data:[DONE]\n\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            });
        };

        var payload = $$"""{"model":"{{ChatModelName}}","messages":[{"role":"user","content":"Hi"}],"stream":true}""";
        var response = await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        // Response must still be valid SSE with model masked
        Assert.IsTrue(body.Contains("data: "), "Response should normalise to 'data: ' (with space)");
        Assert.IsTrue(body.Contains(ChatModelName), "Virtual model name must appear in response");
        Assert.IsTrue(body.Contains("NoSpace"), "Content must be preserved");

        // Verify the data line is valid JSON
        var firstDataLine = body.Split('\n')
            .First(l => l.StartsWith("data: ") && l != "data: [DONE]");
        var jsonPayload = firstDataLine["data: ".Length..];
        var chunk = JsonNode.Parse(jsonPayload);
        Assert.IsNotNull(chunk, "Output must be valid JSON");
        Assert.AreEqual(ChatModelName, chunk["model"]?.ToString());
    }

    [TestMethod]
    public async Task OpenAIToOpenAIBackend_Streaming_ReplaceModelFieldHandlesRegexCharsSafely()
    {
        // The ReplaceModelField method uses Regex.Replace internally. The physical
        // model name (the pattern to match) and the virtual model name (the
        // replacement) are both injected into regex patterns. Verify end-to-end
        // that model names containing characters used by regex (e.g. dots, which
        // are common in OpenAI model names like "gpt-4o-mini") are handled safely.
        MockUpstreamState.Handler = (_, _) =>
        {
            // Physical model "gpt-4o-mini" contains hyphens, and the regex must
            // match it literally (no character-class interpretation).
            var sse = $"data: {{\"model\":\"{PhysicalModelName}\",\"id\":\"x\",\"object\":\"chat.completion.chunk\",\"choices\":[{{\"index\":0,\"delta\":{{\"content\":\"ok\"}},\"finish_reason\":\"stop\"}}]}}\n\ndata: [DONE]\n\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            });
        };

        var payload = $$"""{"model":"{{ChatModelName}}","messages":[{"role":"user","content":"Hi"}],"stream":true}""";
        var response = await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        // The physical model name "gpt-4o-mini" must be replaced with the virtual
        // model name.  Neither the hyphen nor the dot should interfere with the
        // regex-based replacement.
        var firstDataLine = body.Split('\n')
            .First(l => l.StartsWith("data: ") && l != "data: [DONE]");
        var chunk = JsonNode.Parse(firstDataLine["data: ".Length..]);
        Assert.AreEqual(ChatModelName, chunk?["model"]?.ToString(),
            "Model name with hyphens/dots must be replaced correctly");
        Assert.IsFalse(firstDataLine.Contains($"\"{PhysicalModelName}\""),
            "Physical model name must not appear in the response");
    }
}
