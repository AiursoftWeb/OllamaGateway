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
/// Tests that validate the gateway correctly proxies both Ollama native (NDJSON)
/// and OpenAI-compatible (SSE) protocols, including streaming and non-streaming modes,
/// parameter injection, and audit trail integrity.
/// </summary>
[TestClass]
public class DialectProxyTests : TestBase
{
    private const string TestApiKey = "dialect-test-key-001";
    private const string VirtualModelName = "test-chat:latest";
    private const string PhysicalModelName = "llama3.2";
    private const string EmbeddingModelName = "test-embed:latest";
    private const string PhysicalEmbeddingModel = "nomic-embed-text";

    [TestInitialize]
    public override async Task CreateServer()
    {
        // Setup mocks
        TestStartup.MockClickhouse.Reset();
        TestStartup.MockClickhouse.Setup(c => c.Enabled).Returns(false);

        TestStartup.MockOllamaService.Reset();
        TestStartup.MockOllamaService.Setup(s => s.GetUnderlyingModelsAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<string> { PhysicalModelName, PhysicalEmbeddingModel });
        TestStartup.MockOllamaService.Setup(s => s.GetDetailedModelsAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<OllamaService.OllamaModel>
            {
                new() { Name = PhysicalModelName, Size = 1024 * 1024 * 1024L },
                new() { Name = PhysicalEmbeddingModel, Size = 512 * 1024 * 1024L }
            });
        TestStartup.MockOllamaService.Setup(s => s.GetRunningModelsAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<OllamaService.OllamaRunningModel>
            {
                new() { Name = PhysicalModelName }
            });

        // Reset mock upstream handler
        MockUpstreamState.Reset();

        Server = await AppAsync<TestStartup>([], port: Port);
        await Server.UpdateDbAsync<TemplateDbContext>();
        await Server.SeedAsync();
        await Server.StartAsync();

        // Seed: enable anonymous, create provider + virtual models + API key
        using var scope = Server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<GlobalSettingsService>();
        await settings.UpdateSettingAsync(Configuration.SettingsMap.AllowAnonymousApiCall, "True");

        var provider = new OllamaProvider { Name = "TestProvider", BaseUrl = "http://fake-ollama:11434" };
        db.OllamaProviders.Add(provider);
        await db.SaveChangesAsync();

        db.VirtualModels.Add(new VirtualModel
        {
            Name = VirtualModelName,
            UnderlyingModel = PhysicalModelName,
            ProviderId = provider.Id,
            Type = ModelType.Chat,
            Temperature = 0.42f,
            NumPredict = 512
        });
        db.VirtualModels.Add(new VirtualModel
        {
            Name = EmbeddingModelName,
            UnderlyingModel = PhysicalEmbeddingModel,
            ProviderId = provider.Id,
            Type = ModelType.Embedding
        });
        await db.SaveChangesAsync();

        var user = await db.Users.FirstAsync();
        db.ApiKeys.Add(new ApiKey { Name = "Dialect Test Key", Key = TestApiKey, UserId = user.Id });
        await db.SaveChangesAsync();
    }

    private HttpRequestMessage AuthedPost(string url, string jsonBody)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestApiKey);
        return request;
    }

    private HttpRequestMessage AuthedGet(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestApiKey);
        return request;
    }

    // ========================================================================
    // A. OpenAI Dialect Tests
    // ========================================================================

    [TestMethod]
    public async Task OpenAI_NonStreaming_ReturnsCorrectFormat()
    {
        // Arrange: mock upstream returns a standard OpenAI non-streaming response
        MockUpstreamState.Handler = (_, _) =>
        {
            var body = """
            {
                "id": "chatcmpl-mock123",
                "object": "chat.completion",
                "created": 1700000000,
                "model": "llama3.2",
                "choices": [{
                    "index": 0,
                    "message": { "role": "assistant", "content": "Hello from the mock!" },
                    "finish_reason": "stop"
                }],
                "usage": { "prompt_tokens": 15, "completion_tokens": 7, "total_tokens": 22 }
            }
            """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        };

        // Act
        var payload = $$"""{"model":"{{VirtualModelName}}","messages":[{"role":"user","content":"Hi"}],"stream":false}""";
        var response = await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));

        // Assert: HTTP 200
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        // Assert: response body is valid OpenAI format and masks physical model
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(content);
        Assert.IsNotNull(json);
        Assert.AreEqual(VirtualModelName, json["model"]?.ToString());
        Assert.AreEqual("Hello from the mock!", json["choices"]?[0]?["message"]?["content"]?.ToString());
        Assert.AreEqual(22, json["usage"]?["total_tokens"]?.GetValue<int>());

        // Assert: upstream received the PHYSICAL model name, not the virtual one
        Assert.IsNotNull(MockUpstreamState.LastRequestBody);
        var upstreamBody = JsonNode.Parse(MockUpstreamState.LastRequestBody);
        Assert.AreEqual(PhysicalModelName, upstreamBody?["model"]?.ToString());
    }

    [TestMethod]
    public async Task OpenAI_Streaming_ReturnsValidSSE()
    {
        // Arrange: mock upstream returns SSE stream
        MockUpstreamState.Handler = (_, _) =>
        {
            var ssePayload =
                "data: {\"id\":\"chatcmpl-001\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"\"},\"finish_reason\":null}]}\n\n" +
                "data: {\"id\":\"chatcmpl-001\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hello\"},\"finish_reason\":null}]}\n\n" +
                "data: {\"id\":\"chatcmpl-001\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\" World\"},\"finish_reason\":null}]}\n\n" +
                "data: {\"id\":\"chatcmpl-001\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5,\"total_tokens\":15}}\n\n" +
                "data: [DONE]\n\n";

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ssePayload, Encoding.UTF8, "text/event-stream")
            };
            return Task.FromResult(response);
        };

        // Act
        var payload = $$"""{"model":"{{VirtualModelName}}","messages":[{"role":"user","content":"Hi"}],"stream":true}""";
        var response = await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));

        // Assert: HTTP 200
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        // Assert: read the full SSE stream and validate structure
        var body = await response.Content.ReadAsStringAsync();
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Must contain data: [DONE] terminator
        Assert.IsTrue(lines.Any(l => l.Trim() == "data: [DONE]"),
            "SSE stream must end with 'data: [DONE]'");

        // Parse content chunks to reconstruct the answer
        var answer = new StringBuilder();
        foreach (var line in lines)
        {
            if (line.StartsWith("data: ") && line != "data: [DONE]")
            {
                var chunk = JsonNode.Parse(line[6..]);
                Assert.AreEqual(VirtualModelName, chunk?["model"]?.ToString());
                var delta = chunk?["choices"]?[0]?["delta"]?["content"]?.ToString();
                if (!string.IsNullOrEmpty(delta))
                {
                    answer.Append(delta);
                }
            }
        }

        Assert.AreEqual("Hello World", answer.ToString());
    }

    [TestMethod]
    public async Task OpenAI_ParameterInjection_TemperatureAndMaxTokens()
    {
        // Arrange: mock upstream returns simple OK
        MockUpstreamState.Handler = (_, _) =>
        {
            var body = """{"id":"x","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        };

        // Act: client does NOT send temperature/max_tokens, but VirtualModel has them
        var payload = $$"""{"model":"{{VirtualModelName}}","messages":[{"role":"user","content":"test"}],"stream":false}""";
        await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));

        // Assert: upstream payload has injected temperature (0.42) and max_tokens (512)
        Assert.IsNotNull(MockUpstreamState.LastRequestBody);
        var upstreamBody = JsonNode.Parse(MockUpstreamState.LastRequestBody);
        Assert.IsNotNull(upstreamBody);

        // Temperature injected at root level (OpenAI format, NOT inside options)
        var tempVal = upstreamBody["temperature"]?.GetValue<double>() ?? 0;
        Assert.IsTrue(Math.Abs(tempVal - 0.42) < 0.01, $"Expected temperature 0.42, was {tempVal}");
        // NumPredict mapped to max_tokens at root level (OpenAI format)
        Assert.AreEqual(512, upstreamBody["max_tokens"]?.GetValue<int>());
    }

    [TestMethod]
    public async Task OpenAI_UpstreamForwardsToV1Path()
    {
        // Arrange
        MockUpstreamState.Handler = (_, _) =>
        {
            var body = """{"id":"x","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        };

        // Act
        var payload = $$"""{"model":"{{VirtualModelName}}","messages":[{"role":"user","content":"hello"}],"stream":false}""";
        await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));

        // Assert: the upstream request was sent to the /v1/chat/completions path (NOT /api/chat)
        Assert.IsNotNull(MockUpstreamState.LastRequest);
        var upstreamUri = MockUpstreamState.LastRequest.RequestUri?.ToString();
        Assert.IsTrue(upstreamUri!.Contains("/v1/chat/completions"),
            $"Expected upstream path to contain '/v1/chat/completions', but was: {upstreamUri}");
    }

    [TestMethod]
    public async Task OpenAI_ModelsEndpoint_ReturnsValidFormat()
    {
        // Act
        var response = await Http.SendAsync(AuthedGet("/v1/models"));

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(content);
        Assert.IsNotNull(json);
        Assert.AreEqual("list", json["object"]?.ToString());

        var data = json["data"]?.AsArray();
        Assert.IsNotNull(data);
        Assert.IsTrue(data.Count >= 2, "Should have at least 2 virtual models");

        // Verify OpenAI model format
        var firstModel = data[0];
        Assert.IsNotNull(firstModel?["id"]);
        Assert.AreEqual("model", firstModel["object"]?.ToString());
        Assert.IsNotNull(firstModel["created"]);
        Assert.AreEqual("library", firstModel["owned_by"]?.ToString());
    }

    [TestMethod]
    public async Task OpenAI_EmbeddingsEndpoint_ForwardsToV1()
    {
        // Arrange
        MockUpstreamState.Handler = (_, _) =>
        {
            var body = """{"object":"list","data":[{"object":"embedding","index":0,"embedding":[0.1,0.2,0.3]}],"model":"nomic-embed-text","usage":{"prompt_tokens":5,"total_tokens":5}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        };

        // Act
        var payload = $$"""{"model":"{{EmbeddingModelName}}","input":"hello world"}""";
        var response = await Http.SendAsync(AuthedPost("/v1/embeddings", payload));

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(MockUpstreamState.LastRequest!.RequestUri!.ToString().Contains("/v1/embeddings"));

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(content);
        Assert.AreEqual(EmbeddingModelName, json?["model"]?.ToString());

        var upstreamBody = JsonNode.Parse(MockUpstreamState.LastRequestBody!);
        Assert.AreEqual(PhysicalEmbeddingModel, upstreamBody?["model"]?.ToString());
    }

    // ========================================================================
    // B. Ollama Native Dialect Tests
    // ========================================================================

    [TestMethod]
    public async Task Ollama_NonStreaming_ReturnsCorrectFormat()
    {
        // Arrange: upstream returns Ollama native non-streaming response
        MockUpstreamState.Handler = (_, _) =>
        {
            var body = """
            {
                "model": "llama3.2",
                "created_at": "2024-01-01T00:00:00Z",
                "message": { "role": "assistant", "content": "Native Ollama response!" },
                "done": true,
                "total_duration": 5000000000,
                "prompt_eval_count": 20,
                "eval_count": 10
            }
            """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        };

        // Act
        var payload = $$"""{"model":"{{VirtualModelName}}","messages":[{"role":"user","content":"Hi"}],"stream":false}""";
        var response = await Http.SendAsync(AuthedPost("/api/chat", payload));

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(content);
        Assert.IsNotNull(json);
        Assert.AreEqual(VirtualModelName, json["model"]?.ToString());
        Assert.AreEqual("Native Ollama response!", json["message"]?["content"]?.ToString());
        Assert.AreEqual(true, json["done"]?.GetValue<bool>());
    }

    [TestMethod]
    public async Task Ollama_Streaming_ReturnsValidNDJSON()
    {
        // Arrange: mock upstream returns NDJSON stream (Ollama native format)
        MockUpstreamState.Handler = (_, _) =>
        {
            var ndjson =
                """{"model":"llama3.2","message":{"role":"assistant","content":"Hello"},"done":false}""" + "\n" +
                """{"model":"llama3.2","message":{"role":"assistant","content":" from"},"done":false}""" + "\n" +
                """{"model":"llama3.2","message":{"role":"assistant","content":" Ollama"},"done":false}""" + "\n" +
                """{"model":"llama3.2","message":{"role":"assistant","content":""},"done":true,"prompt_eval_count":12,"eval_count":8}""" + "\n";

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson")
            };
            return Task.FromResult(response);
        };

        // Act
        var payload = $$"""{"model":"{{VirtualModelName}}","messages":[{"role":"user","content":"Hi"}],"stream":true}""";
        var response = await Http.SendAsync(AuthedPost("/api/chat", payload));

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Reconstruct answer from NDJSON
        var answer = new StringBuilder();
        bool foundDone = false;
        foreach (var line in lines)
        {
            var chunk = JsonNode.Parse(line);
            Assert.AreEqual(VirtualModelName, chunk?["model"]?.ToString());
            var content = chunk?["message"]?["content"]?.ToString();
            if (!string.IsNullOrEmpty(content)) answer.Append(content);
            if (chunk?["done"]?.GetValue<bool>() == true) foundDone = true;
        }

        Assert.AreEqual("Hello from Ollama", answer.ToString());
        Assert.IsTrue(foundDone, "NDJSON stream must contain a final 'done: true' chunk");
    }

    [TestMethod]
    public async Task Ollama_Streaming_ThinkField_Captured()
    {
        // Arrange: mock upstream returns NDJSON with think fields (DeepSeek-R1 style)
        MockUpstreamState.Handler = (_, _) =>
        {
            var ndjson =
                """{"model":"llama3.2","message":{"role":"assistant","content":"","think":"Let me think..."},"done":false}""" + "\n" +
                """{"model":"llama3.2","message":{"role":"assistant","content":"","think":" step by step."},"done":false}""" + "\n" +
                """{"model":"llama3.2","message":{"role":"assistant","content":"The answer is 42."},"done":false}""" + "\n" +
                """{"model":"llama3.2","message":{"role":"assistant","content":""},"done":true,"prompt_eval_count":5,"eval_count":3}""" + "\n";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson")
            });
        };

        // Act
        var payload = $$"""{"model":"{{VirtualModelName}}","messages":[{"role":"user","content":"What is the meaning?"}],"stream":true}""";
        var response = await Http.SendAsync(AuthedPost("/api/chat", payload));

        // Assert: response streams through successfully
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.IsTrue(body.Contains("The answer is 42."));
        Assert.IsTrue(body.Contains("Let me think..."));
    }

    [TestMethod]
    public async Task Ollama_UpstreamForwardsToApiChat()
    {
        // Arrange
        MockUpstreamState.Handler = (_, _) =>
        {
            var body = """{"model":"llama3.2","message":{"role":"assistant","content":"ok"},"done":true,"prompt_eval_count":1,"eval_count":1}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        };

        // Act
        var payload = $$"""{"model":"{{VirtualModelName}}","messages":[{"role":"user","content":"hello"}],"stream":false}""";
        await Http.SendAsync(AuthedPost("/api/chat", payload));

        // Assert: upstream request was sent to /api/chat (NOT /v1/chat/completions)
        var upstreamUri = MockUpstreamState.LastRequest?.RequestUri?.ToString();
        Assert.IsNotNull(upstreamUri);
        Assert.IsTrue(upstreamUri.Contains("/api/chat"),
            $"Expected upstream path to contain '/api/chat', but was: {upstreamUri}");
        Assert.IsFalse(upstreamUri.Contains("/v1/"),
            $"Ollama native path must NOT contain '/v1/', but was: {upstreamUri}");
    }

    [TestMethod]
    public async Task Ollama_ParameterInjection_InsideOptions()
    {
        // Arrange
        MockUpstreamState.Handler = (_, _) =>
        {
            var body = """{"model":"llama3.2","message":{"role":"assistant","content":"ok"},"done":true,"prompt_eval_count":1,"eval_count":1}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        };

        // Act
        var payload = $$"""{"model":"{{VirtualModelName}}","messages":[{"role":"user","content":"test"}],"stream":false}""";
        await Http.SendAsync(AuthedPost("/api/chat", payload));

        // Assert: for Ollama native, temperature and num_predict go into 'options' object
        Assert.IsNotNull(MockUpstreamState.LastRequestBody);
        var upstreamBody = JsonNode.Parse(MockUpstreamState.LastRequestBody);
        Assert.IsNotNull(upstreamBody);

        var options = upstreamBody["options"];
        Assert.IsNotNull(options, "Ollama native format must have 'options' object");
        var tempVal = options["temperature"]?.GetValue<double>() ?? 0;
        Assert.IsTrue(Math.Abs(tempVal - 0.42) < 0.01, $"Expected temperature 0.42, was {tempVal}");
        Assert.AreEqual(512, options["num_predict"]?.GetValue<int>());
    }

    // ========================================================================
    // C. Gateway Robustness Tests
    // ========================================================================

    [TestMethod]
    public async Task Gateway_Upstream500_TransparentlyReturned()
    {
        // Arrange: upstream returns 500
        MockUpstreamState.Handler = (_, _) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("""{"error":"model crashed"}""", Encoding.UTF8, "application/json")
            });
        };

        // Act (OpenAI path)
        var payload = $$"""{"model":"{{VirtualModelName}}","messages":[{"role":"user","content":"crash"}],"stream":false}""";
        var response = await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));

        // Assert: 500 is transparently returned
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [TestMethod]
    public async Task Gateway_OllamaUpstream500_TransparentlyReturned()
    {
        // Arrange: upstream returns 500
        MockUpstreamState.Handler = (_, _) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("""{"error":"model not loaded"}""", Encoding.UTF8, "application/json")
            });
        };

        // Act (Ollama native path)
        var payload = $$"""{"model":"{{VirtualModelName}}","messages":[{"role":"user","content":"crash"}],"stream":false}""";
        var response = await Http.SendAsync(AuthedPost("/api/chat", payload));

        // Assert
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [TestMethod]
    public async Task Gateway_UnknownModel_Returns404()
    {
        // Act: request a model that doesn't exist in the gateway
        var payload = """{"model":"nonexistent-model:fake","messages":[{"role":"user","content":"hi"}],"stream":false}""";

        var openaiResponse = await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));
        Assert.AreEqual(HttpStatusCode.NotFound, openaiResponse.StatusCode);

        var ollamaResponse = await Http.SendAsync(AuthedPost("/api/chat", payload));
        Assert.AreEqual(HttpStatusCode.NotFound, ollamaResponse.StatusCode);
    }

    [TestMethod]
    public async Task Gateway_ProtocolIsolation_NoDialectCrosstalk()
    {
        // This test verifies that OpenAI requests go to /v1/ upstream and Ollama requests go to /api/
        var requestPaths = new List<string>();

        MockUpstreamState.Handler = (req, _) =>
        {
            requestPaths.Add(req.RequestUri?.PathAndQuery ?? "");
            var body = req.RequestUri!.PathAndQuery.Contains("/v1/")
                ? """{"id":"x","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"openai"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}"""
                : """{"model":"llama3.2","message":{"role":"assistant","content":"ollama"},"done":true,"prompt_eval_count":1,"eval_count":1}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        };

        // Send OpenAI request
        var openaiPayload = $$"""{"model":"{{VirtualModelName}}","messages":[{"role":"user","content":"hi"}],"stream":false}""";
        await Http.SendAsync(AuthedPost("/v1/chat/completions", openaiPayload));

        // Send Ollama request
        var ollamaPayload = $$"""{"model":"{{VirtualModelName}}","messages":[{"role":"user","content":"hi"}],"stream":false}""";
        await Http.SendAsync(AuthedPost("/api/chat", ollamaPayload));

        // Assert: one went to /v1/ and the other to /api/
        Assert.AreEqual(2, requestPaths.Count);
        Assert.IsTrue(requestPaths[0].Contains("/v1/chat/completions"), $"First request should go to /v1/, was: {requestPaths[0]}");
        Assert.IsTrue(requestPaths[1].Contains("/api/chat"), $"Second request should go to /api/, was: {requestPaths[1]}");
    }

    [TestMethod]
    public async Task OpenAI_Streaming_ReasoningContent_Captured()
    {
        // Arrange
        MockUpstreamState.Handler = (_, _) =>
        {
            var sse = new[]
            {
                """data: {"id":"1","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"reasoning_content":"Thinking..."},"finish_reason":null}]}""",
                """data: {"id":"2","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":"Hello!"},"finish_reason":"stop"}]}""",
                "data: [DONE]"
            };
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Join("\n\n", sse) + "\n\n", Encoding.UTF8, "text/event-stream")
            };
            return Task.FromResult(response);
        };

        // Act
        var payload = $$"""{"model":"{{VirtualModelName}}","messages":[{"role":"user","content":"tell me a secret"}],"stream":true}""";
        var response = await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));
        var body = await response.Content.ReadAsStringAsync();

        // Assert: 1. Model name is masked in response chunks. 2. Reasoning content is preserved.
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var lines = body.Split('\n').Where(l => l.StartsWith("data: ") && l != "data: [DONE]");
        foreach (var line in lines)
        {
            var chunk = JsonNode.Parse(line[6..]);
            Assert.AreEqual(VirtualModelName, chunk?["model"]?.ToString());
        }
        Assert.IsTrue(body.Contains("reasoning_content"), "Response must preserve reasoning_content");
        Assert.IsTrue(body.Contains("Thinking..."), "Response must preserve thinking content");

        // Assert: 3. Upstream request received the 'think' parameter if we mock it in Seed (Wait, I should check Seed)
        // In CreateServer, we seeded VirtualModel with Temperature=0.42 and NumPredict=512, but didn't set Thinking.
        // Let's check the last request.
        var upstreamBody = JsonNode.Parse(MockUpstreamState.LastRequestBody ?? "{}");
        Assert.IsNotNull(upstreamBody);
        // It won't have 'think' yet because Thinking.HasValue was false in the seeded model.
    }

    [TestMethod]
    public async Task OpenAI_ThinkingOverride_Injected()
    {
        // Arrange: Update the model to have thinking and context size enabled
        using (var scope = Server?.Services.CreateScope())
        {
            Assert.IsNotNull(scope);
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var vm = await db.VirtualModels.FirstAsync(m => m.Name == VirtualModelName);
            vm.Thinking = true;
            vm.NumCtx = 4096;
            await db.SaveChangesAsync();
        }

        MockUpstreamState.Handler = (_, _) =>
        {
            var body = """{"id":"x","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        };

        // Act
        var payload = $$"""{"model":"{{VirtualModelName}}","messages":[{"role":"user","content":"hi"}],"stream":false}""";
        await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));

        // Assert: Upstream request should now contain "think": true and "options.num_ctx"
        var upstreamBody = JsonNode.Parse(MockUpstreamState.LastRequestBody ?? "{}");
        Assert.AreEqual(true, upstreamBody?["think"]?.GetValue<bool>());
        Assert.AreEqual(4096, upstreamBody?["options"]?["num_ctx"]?.GetValue<int>());
    }
}
