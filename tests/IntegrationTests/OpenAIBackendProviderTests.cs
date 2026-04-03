using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Aiursoft.DbTools;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Services;
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
                "data: {\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"model\":\"gpt-4o-mini\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":1,\"total_tokens\":6}}\n\n" +
                "data: [DONE]\n\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            });
        };

        var payload = $$"""{"model":"{{ChatModelName}}","messages":[{"role":"user","content":"Hi"}],"stream":true}""";
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
}
