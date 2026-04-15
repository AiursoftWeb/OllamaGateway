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
        TestStartup.MockOllamaService.Setup(s => s.GetUnderlyingModelsAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<string> { PhysicalModelName, PhysicalEmbeddingModel });
        TestStartup.MockOllamaService.Setup(s => s.GetDetailedModelsAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<OllamaService.OllamaModel>
            {
                new() { Name = PhysicalModelName, Size = 1024 * 1024 * 1024L },
                new() { Name = PhysicalEmbeddingModel, Size = 512 * 1024 * 1024L }
            });
        TestStartup.MockOllamaService.Setup(s => s.GetRunningModelsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>()))
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

        var chatModel = new VirtualModel
        {
            Name = VirtualModelName,
            Type = ModelType.Chat,
            Temperature = 0.42f,
            NumPredict = 512
        };
        chatModel.VirtualModelBackends.Add(new VirtualModelBackend
        {
            ProviderId = provider.Id,
            UnderlyingModelName = PhysicalModelName,
            Enabled = true,
            IsHealthy = true
        });
        db.VirtualModels.Add(chatModel);

        var embedModel = new VirtualModel
        {
            Name = EmbeddingModelName,
            Type = ModelType.Embedding
        };
        embedModel.VirtualModelBackends.Add(new VirtualModelBackend
        {
            ProviderId = provider.Id,
            UnderlyingModelName = PhysicalEmbeddingModel,
            Enabled = true,
            IsHealthy = true
        });
        db.VirtualModels.Add(embedModel);
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
        // Arrange: mock upstream returns a standard Ollama native non-streaming response
        MockUpstreamState.Handler = (_, _) =>
        {
            var body = """
            {
                "model": "llama3.2",
                "created_at": "2024-01-01T00:00:00Z",
                "message": { "role": "assistant", "content": "Hello from the mock!" },
                "done": true,
                "total_duration": 5000000000,
                "prompt_eval_count": 15,
                "eval_count": 7
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

        // Assert: upstream received the PHYSICAL model name and the request was translated to /api/chat
        Assert.IsNotNull(MockUpstreamState.LastRequestBody);
        var upstreamBody = JsonNode.Parse(MockUpstreamState.LastRequestBody);
        Assert.AreEqual(PhysicalModelName, upstreamBody?["model"]?.ToString());
        Assert.IsTrue(MockUpstreamState.LastRequest?.RequestUri?.AbsolutePath.EndsWith("/api/chat") ?? false);
    }

    [TestMethod]
    public async Task OpenAI_Streaming_ReturnsValidSSE()
    {
        // Arrange: mock upstream returns NDJSON stream (Ollama native format)
        MockUpstreamState.Handler = (_, _) =>
        {
            var ndjson =
                """{"model":"llama3.2","message":{"role":"assistant","content":"Hello"},"done":false}""" + "\n" +
                """{"model":"llama3.2","message":{"role":"assistant","content":" World"},"done":false}""" + "\n" +
                """{"model":"llama3.2","message":{"role":"assistant","content":""},"done":true,"prompt_eval_count":10,"eval_count":5}""" + "\n";

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson")
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
    public async Task OpenAI_ParameterInjection_TemperatureAndNumPredict()
    {
        // Arrange: mock upstream returns simple OK
        MockUpstreamState.Handler = (_, _) =>
        {
            var body = """{"model":"llama3.2","message":{"role":"assistant","content":"ok"},"done":true,"prompt_eval_count":1,"eval_count":1}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        };

        // Act: client does NOT send temperature/max_tokens, but VirtualModel has them
        var payload = $$"""{"model":"{{VirtualModelName}}","messages":[{"role":"user","content":"test"}],"stream":false}""";
        await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));

        // Assert: upstream payload has injected temperature (0.42) and num_predict (512) inside options
        Assert.IsNotNull(MockUpstreamState.LastRequestBody);
        var upstreamBody = JsonNode.Parse(MockUpstreamState.LastRequestBody);
        Assert.IsNotNull(upstreamBody);

        var options = upstreamBody["options"];
        Assert.IsNotNull(options, "Ollama native format must have 'options' object");
        var tempVal = options["temperature"]?.GetValue<double>() ?? 0;
        Assert.IsTrue(Math.Abs(tempVal - 0.42) < 0.01, $"Expected temperature 0.42, was {tempVal}");
        // NumPredict mapped to num_predict in options
        Assert.AreEqual(512, options["num_predict"]?.GetValue<int>());
    }

    [TestMethod]
    public async Task OpenAI_UpstreamForwardsToApiChat()
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
        await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));

        // Assert: the upstream request was sent to the /api/chat path (NOT /v1/chat/completions)
        Assert.IsNotNull(MockUpstreamState.LastRequest);
        var upstreamUri = MockUpstreamState.LastRequest.RequestUri?.ToString();
        Assert.IsTrue(upstreamUri!.Contains("/api/chat"),
            $"Expected upstream path to contain '/api/chat', but was: {upstreamUri}");
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
    public async Task OpenAI_EmbeddingsEndpoint_ForwardsToApiEmbed()
    {
        // Arrange
        MockUpstreamState.Handler = (_, _) =>
        {
            var body = """{"model":"nomic-embed-text","embeddings":[[0.1,0.2,0.3]],"prompt_eval_count":5}""";
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
        Assert.IsTrue(MockUpstreamState.LastRequest!.RequestUri!.ToString().Contains("/api/embed"));

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(content);
        Assert.AreEqual(EmbeddingModelName, json?["model"]?.ToString());
        Assert.AreEqual("list", json?["object"]?.ToString());

        var upstreamBody = JsonNode.Parse(MockUpstreamState.LastRequestBody!);
        Assert.AreEqual(PhysicalEmbeddingModel, upstreamBody?["model"]?.ToString());
        Assert.IsNotNull(upstreamBody?["input"]);
    }

    // ========================================================================
    [TestMethod]
    public async Task OpenAI_ParameterInjection_TopKFromDbIsForwardedToOllamaBackend()
    {
        // Regression test for path ③ (Ollama provider + OpenAI API via /v1/chat/completions):
        // OpenAIController's Ollama-backend code path was not injecting virtualModel.TopK into
        // the outbound Ollama options object, silently discarding DB-configured top_k values.
        const string topKModelName = "topk-regression:latest";

        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var provider = await db.OllamaProviders.FirstAsync();

            var model = new VirtualModel
            {
                Name = topKModelName,
                Type = ModelType.Chat,
                TopK = 30
            };
            model.VirtualModelBackends.Add(new VirtualModelBackend
            {
                ProviderId = provider.Id,
                UnderlyingModelName = PhysicalModelName,
                Enabled = true,
                IsHealthy = true
            });
            db.VirtualModels.Add(model);
            await db.SaveChangesAsync();
        }

        MockUpstreamState.Handler = (_, _) =>
        {
            const string body =
                """{"model":"llama3.2","message":{"role":"assistant","content":"ok"},"done":true,"prompt_eval_count":1,"eval_count":1}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        };

        var payload = $$"""{ "model":"{{topKModelName}}","messages":[{"role":"user","content":"test"}],"stream":false}""";
        await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));

        Assert.IsNotNull(MockUpstreamState.LastRequestBody,
            "Upstream should have received a request body");
        var upstreamBody = JsonNode.Parse(MockUpstreamState.LastRequestBody);
        var options = upstreamBody?["options"];
        Assert.IsNotNull(options, "Ollama format must have an 'options' object when DB params are set");
        Assert.AreEqual(30, options["top_k"]?.GetValue<int>(),
            "top_k from the virtual model DB config must be injected into the Ollama upstream request "
          + "when the client calls via the OpenAI-compatible endpoint (/v1/chat/completions)");
    }

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

    [TestMethod]
    public async Task Ollama_Generate_ForwardsToApiGenerate()
    {
        // Arrange
        MockUpstreamState.Handler = (req, _) =>
        {
            if (req.RequestUri?.PathAndQuery.Contains("/api/generate") == true)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"model":"test-model","response":"hello","done":true}""", Encoding.UTF8, "application/json")
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        };

        // Act
        var payload = $$"""{"model":"{{VirtualModelName}}","prompt":"test","stream":false}""";
        var response = await Http.SendAsync(AuthedPost("/api/generate", payload));

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.IsTrue(content.Contains("hello"));
        Assert.AreEqual("/api/generate", MockUpstreamState.LastRequest?.RequestUri?.AbsolutePath);
    }

    [TestMethod]
    public async Task Ollama_KeepAlive_Injected()
    {
        // Arrange
        MockUpstreamState.Handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"model":"test-model","response":"ok","done":true}""", Encoding.UTF8, "application/json")
        });

        // Act
        var payload = $$"""{"model":"{{VirtualModelName}}","prompt":"test","stream":false}""";
        await Http.SendAsync(AuthedPost("/api/generate", payload));

        // Assert
        Assert.IsNotNull(MockUpstreamState.LastRequestBody);
        var upstreamBody = JsonNode.Parse(MockUpstreamState.LastRequestBody);
        Assert.AreEqual("5m", upstreamBody?["keep_alive"]?.ToString());
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
    public async Task Gateway_ProtocolIsolation_BothForwardToApiChat()
    {
        // Both OpenAI and Ollama dialects now translate to /api/chat upstream
        var requestPaths = new List<string>();

        MockUpstreamState.Handler = (req, _) =>
        {
            requestPaths.Add(req.RequestUri?.PathAndQuery ?? "");
            var body = """{"model":"llama3.2","message":{"role":"assistant","content":"ok"},"done":true,"prompt_eval_count":1,"eval_count":1}""";
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

        // Assert: both went to /api/chat
        Assert.AreEqual(2, requestPaths.Count);
        Assert.IsTrue(requestPaths[0].Contains("/api/chat"), $"First request should go to /api/chat, was: {requestPaths[0]}");
        Assert.IsTrue(requestPaths[1].Contains("/api/chat"), $"Second request should go to /api/chat, was: {requestPaths[1]}");
    }

    [TestMethod]
    public async Task OpenAI_Streaming_ReasoningContent_Captured()
    {
        // Arrange: Upstream returns Ollama NDJSON with 'think' field
        MockUpstreamState.Handler = (_, _) =>
        {
            var ndjson =
                """{"model":"llama3.2","message":{"role":"assistant","content":"","think":"Thinking..."},"done":false}""" + "\n" +
                """{"model":"llama3.2","message":{"role":"assistant","content":"Hello!"},"done":true,"prompt_eval_count":10,"eval_count":5}""" + "\n";
            
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson")
            };
            return Task.FromResult(response);
        };

        // Act
        var payload = $$"""{"model":"{{VirtualModelName}}","messages":[{"role":"user","content":"tell me a secret"}],"stream":true}""";
        var response = await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));
        var body = await response.Content.ReadAsStringAsync();

        // Assert: 1. Model name is masked in response chunks. 2. Reasoning content is translated to reasoning_content.
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var lines = body.Split('\n').Where(l => l.StartsWith("data: ") && l != "data: [DONE]");
        foreach (var line in lines)
        {
            var chunk = JsonNode.Parse(line[6..]);
            Assert.AreEqual(VirtualModelName, chunk?["model"]?.ToString());
        }
        Assert.IsTrue(body.Contains("reasoning_content"), "Response must contain translated reasoning_content");
        Assert.IsTrue(body.Contains("Thinking..."), "Response must contain thinking content");
    }

    [TestMethod]
    public async Task OpenAI_Streaming_ThinkingFieldName_Captured()
    {
        // Arrange: Upstream returns Ollama NDJSON with 'thinking' field (instead of 'think')
        MockUpstreamState.Handler = (_, _) =>
        {
            var ndjson =
                """{"model":"llama3.2","message":{"role":"assistant","content":"","thinking":"Thinking..."},"done":false}""" + "\n" +
                """{"model":"llama3.2","message":{"role":"assistant","content":"Hello!"},"done":true,"prompt_eval_count":10,"eval_count":5}""" + "\n";
            
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson")
            };
            return Task.FromResult(response);
        };

        // Act
        var payload = $$"""{"model":"{{VirtualModelName}}","messages":[{"role":"user","content":"hi"}],"stream":true}""";
        var response = await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));
        var body = await response.Content.ReadAsStringAsync();

        // Assert: Reasoning content is captured from 'thinking' and translated to 'reasoning_content'
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(body.Contains("reasoning_content"), "Response must contain translated reasoning_content");
        Assert.IsTrue(body.Contains("Thinking..."), "Response must contain thinking content from 'thinking' field");
    }

    [TestMethod]
    public async Task OpenAI_AdvancedContentArray_Flattened()
    {
        // 1. Arrange: Send an OpenAI-style content array (common in Opencode/Cursor)
        MockUpstreamState.Handler = (_, _) =>
        {
            var body = """{"model":"llama3.2","message":{"role":"assistant","content":"I see your plan."}, "done":true, "prompt_eval_count":5, "eval_count":3}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        };

        var payload = $$"""
        {
            "model": "{{VirtualModelName}}",
            "messages": [
                {
                    "role": "user",
                    "content": [
                        { "type": "text", "text": "Part 1. " },
                        { "type": "text", "text": "Part 2." }
                    ]
                }
            ],
            "stream": false
        }
        """;

        // 2. Act
        await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));

        // 3. Assert: Upstream should receive a single flattened string "Part 1. Part 2."
        Assert.IsNotNull(MockUpstreamState.LastRequestBody);
        var upstreamBody = JsonNode.Parse(MockUpstreamState.LastRequestBody);
        var translatedContent = upstreamBody?["messages"]?[0]?["content"]?.ToString();
        Assert.AreEqual("Part 1. Part 2.", translatedContent);
    }

    [TestMethod]
    public async Task OpenAI_MultiModal_ImagesExtracted()
    {
        // 1. Arrange: Send an OpenAI-style content array with an image
        MockUpstreamState.Handler = (_, _) =>
        {
            var body = """{"model":"llama3.2","message":{"role":"assistant","content":"Nice picture!"}, "done":true, "prompt_eval_count":5, "eval_count":3}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        };

        var payload = $$"""
        {
            "model": "{{VirtualModelName}}",
            "messages": [
                {
                    "role": "user",
                    "content": [
                        { "type": "text", "text": "What is in this image?" },
                        { "type": "image_url", "image_url": { "url": "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8/5+hHgAHggJ/PchI7wAAAABJRU5ErkJggg==" } }
                    ]
                }
            ],
            "stream": false
        }
        """;

        // 2. Act
        await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));

        // 3. Assert: Upstream should receive content string AND images array
        Assert.IsNotNull(MockUpstreamState.LastRequestBody);
        var upstreamBody = JsonNode.Parse(MockUpstreamState.LastRequestBody);
        var message = upstreamBody?["messages"]?[0];
        
        Assert.AreEqual("What is in this image?", message?["content"]?.ToString());
        var images = message?["images"]?.AsArray();
        Assert.IsNotNull(images);
        Assert.AreEqual(1, images.Count);
        Assert.AreEqual("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8/5+hHgAHggJ/PchI7wAAAABJRU5ErkJggg==", images[0]?.ToString());
    }

    [TestMethod]
    public async Task OpenAI_ToolCallHistory_ArgumentsParsed()
    {
        // 1. Arrange: Send a message with a tool call history (OpenAI-style string arguments)
        MockUpstreamState.Handler = (_, _) =>
        {
            var body = """{"model":"llama3.2","message":{"role":"assistant","content":"Done."}, "done":true}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        };

        var payload = $$"""
        {
            "model": "{{VirtualModelName}}",
            "messages": [
                {
                    "role": "assistant",
                    "content": null,
                    "tool_calls": [
                        {
                            "id": "call_123",
                            "type": "function",
                            "function": {
                                "name": "get_weather",
                                "arguments": "{\"location\":\"London\"}"
                            }
                        }
                    ]
                }
            ],
            "stream": false
        }
        """;

        // 2. Act
        await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));

        // 3. Assert: Upstream should receive arguments as a REAL JSON OBJECT (Ollama format)
        Assert.IsNotNull(MockUpstreamState.LastRequestBody);
        var upstreamBody = JsonNode.Parse(MockUpstreamState.LastRequestBody);
        var toolCall = upstreamBody?["messages"]?[0]?["tool_calls"]?[0];
        
        Assert.AreEqual("get_weather", toolCall?["function"]?["name"]?.ToString());
        Assert.AreEqual("call_123", toolCall?["id"]?.ToString());
        Assert.AreEqual("function", toolCall?["type"]?.ToString());
        var args = toolCall?["function"]?["arguments"];
        Assert.IsInstanceOfType(args, typeof(JsonObject)); // Is NOT a string!
        Assert.AreEqual("London", args["location"]?.ToString());
    }

    [TestMethod]
    public async Task OpenAI_Response_ToolCallsStringified()
    {
        // 1. Arrange: Upstream returns an Ollama-style tool call (Object arguments)
        MockUpstreamState.Handler = (_, _) =>
        {
            var body = """
            {
                "model": "llama3.2",
                "message": {
                    "role": "assistant",
                    "content": "",
                    "tool_calls": [
                        {
                            "function": {
                                "name": "run_command",
                                "arguments": { "command": "ls -la" }
                            }
                        }
                    ]
                },
                "done": true
            }
            """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        };

        var payload = $$"""{"model": "{{VirtualModelName}}", "messages": [{"role": "user", "content": "list files"}], "stream": false}""";

        // 2. Act
        var response = await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));
        var responseBody = await response.Content.ReadAsStringAsync();

        // 3. Assert: Gateway should return OpenAI-style tool calls (STRING arguments)
        var json = JsonNode.Parse(responseBody);
        var toolCall = json?["choices"]?[0]?["message"]?["tool_calls"]?[0];
        
        Assert.AreEqual("run_command", toolCall?["function"]?["name"]?.ToString());
        var args = toolCall?["function"]?["arguments"]?.ToString();
        Assert.AreEqual("{\"command\":\"ls -la\"}", args);
        Assert.AreEqual("tool_calls", json?["choices"]?[0]?["finish_reason"]?.ToString());
    }

    [TestMethod]
    public async Task OpenAI_Streaming_FirstChunk_HasRoleAssistant()
    {
        // 1. Arrange
        MockUpstreamState.Handler = (_, _) =>
        {
            var ndjson = """{"model":"llama3.2","message":{"role":"assistant","content":"Hi"},"done":true}""" + "\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson")
            });
        };

        // 2. Act
        var payload = $$"""{"model":"{{VirtualModelName}}","messages":[{"role":"user","content":"hi"}],"stream":true}""";
        var response = await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));
        var body = await response.Content.ReadAsStringAsync();

        // 3. Assert: First chunk should have role: assistant
        var lines = body.Split('\n').Where(l => l.StartsWith("data: ") && l != "data: [DONE]").ToList();
        Assert.IsTrue(lines.Any());
        var firstChunk = JsonNode.Parse(lines[0][6..]);
        Assert.AreEqual("assistant", firstChunk?["choices"]?[0]?["delta"]?["role"]?.ToString());
    }

    [TestMethod]
    public async Task OpenAI_Streaming_ToolCalls_HasCorrectFinishReason()
    {
        // 1. Arrange: Two chunks. First chunk has tool calls. Second chunk is empty and says "done: true".
        MockUpstreamState.Handler = (_, _) =>
        {
            var ndjson = 
                """{"model":"llama3.2","message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"test","arguments":{}}}]},"done":false}""" + "\n" +
                """{"model":"llama3.2","done":true}""" + "\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson")
            });
        };

        // 2. Act
        var payload = $$"""{"model":"{{VirtualModelName}}","messages":[{"role":"user","content":"hi"}],"stream":true}""";
        var response = await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));
        var body = await response.Content.ReadAsStringAsync();

        // 3. Assert: Final chunk should STILL have finish_reason: tool_calls (persistent flag fix)
        var lines = body.Split('\n').Where(l => l.StartsWith("data: ") && l != "data: [DONE]").ToList();
        Assert.AreEqual(2, lines.Count);
        var lastChunk = JsonNode.Parse(lines.Last()[6..]);
        Assert.AreEqual("tool_calls", lastChunk?["choices"]?[0]?["finish_reason"]?.ToString());
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
            var body = """{"model":"llama3.2","message":{"role":"assistant","content":"ok","think":"Let me think"},"done":true,"prompt_eval_count":1,"eval_count":1}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        };

        // Act
        var payload = $$"""{"model":"{{VirtualModelName}}","messages":[{"role":"user","content":"hi"}],"stream":false}""";
        await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));

        // Assert: 1. Target URL should be /api/chat (due to protocol translation)
        Assert.IsTrue(MockUpstreamState.LastRequest?.RequestUri?.AbsolutePath.EndsWith("/api/chat") ?? false, "Request should be translated to /api/chat");

        // Assert: 2. Upstream request should now contain "think": true and "options.num_ctx"
        var upstreamBody = JsonNode.Parse(MockUpstreamState.LastRequestBody ?? "{}");
        Assert.AreEqual(true, upstreamBody?["think"]?.GetValue<bool>());
        Assert.AreEqual(4096, upstreamBody?["options"]?["num_ctx"]?.GetValue<int>());
    }

    [TestMethod]
    public async Task Gateway_CircuitBreaker_FallbackToNextNode()
    {
        // Arrange: Add a second backend (Priority 1)
        using (var scope = Server?.Services.CreateScope())
        {
            Assert.IsNotNull(scope);
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            
            var provider2 = new OllamaProvider { Name = "FallbackProvider", BaseUrl = "http://fallback-ollama:11434" };
            db.OllamaProviders.Add(provider2);
            await db.SaveChangesAsync();

            var vm = await db.VirtualModels.FirstAsync(m => m.Name == VirtualModelName);
            vm.VirtualModelBackends.Add(new VirtualModelBackend
            {
                ProviderId = provider2.Id,
                UnderlyingModelName = "fallback-model",
                Priority = 1,
                Enabled = true,
                IsHealthy = true
            });
            await db.SaveChangesAsync();
        }

        var failCount = 0;
        MockUpstreamState.Handler = (req, _) =>
        {
            if (req.RequestUri!.ToString().Contains("fake-ollama"))
            {
                failCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("""{"error":"model crashed"}""", Encoding.UTF8, "application/json")
                });
            }

            // If it hits fallback-ollama
            var body = """{"model":"fallback-model","message":{"role":"assistant","content":"Fallback success!"},"done":true,"prompt_eval_count":1,"eval_count":1}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        };

        var payload = $$"""{"model":"{{VirtualModelName}}","messages":[{"role":"user","content":"test fallback"}],"stream":false}""";

        // Act 1: First request hits the primary node 3 times, fails, and bans it.
        var response1 = await Http.SendAsync(AuthedPost("/api/chat", payload));
        Assert.AreEqual(HttpStatusCode.InternalServerError, response1.StatusCode);
        Assert.AreEqual(3, failCount); // MaxRetries exhausted on Node 1

        // Act 2: Second request hits the fallback node because Node 1 is now in circuit breaker ban.
        var response2 = await Http.SendAsync(AuthedPost("/api/chat", payload));
        Assert.AreEqual(HttpStatusCode.OK, response2.StatusCode);
        
        var body2 = await response2.Content.ReadAsStringAsync();
        var json2 = JsonNode.Parse(body2);
        Assert.AreEqual("Fallback success!", json2?["message"]?["content"]?.ToString());
    }

    // ========================================================================
    // F. Ollama Native Embed Endpoint Tests
    // ========================================================================

    [TestMethod]
    public async Task Ollama_Embed_StringInput_PreservedUpstream()
    {
        // Arrange: mock upstream returns a valid embedding response
        MockUpstreamState.Handler = (_, _) =>
        {
            var body = """{"model":"nomic-embed-text","embeddings":[[0.1,0.2,0.3]],"prompt_eval_count":3}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        };

        // Act: send a string input (the exact scenario that was broken before the JsonNode fix)
        var payload = $$"""{"model":"{{EmbeddingModelName}}","input":"hello world"}""";
        var response = await Http.SendAsync(AuthedPost("/api/embed", payload));

        // Assert: upstream received the request and the 'input' field is still a string (not an array)
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(MockUpstreamState.LastRequest!.RequestUri!.ToString().Contains("/api/embed"));

        Assert.IsNotNull(MockUpstreamState.LastRequestBody);
        var upstreamBody = JsonNode.Parse(MockUpstreamState.LastRequestBody);
        Assert.IsNotNull(upstreamBody);

        // The critical assertion: 'input' must be a string value, not an array
        var inputNode = upstreamBody["input"];
        Assert.IsNotNull(inputNode, "Upstream body must contain 'input' field");
        Assert.AreEqual("hello world", inputNode.GetValue<string>(), "Input must be preserved as a string");

        // Model name must be translated to physical model
        Assert.AreEqual(PhysicalEmbeddingModel, upstreamBody["model"]?.ToString());
    }

    [TestMethod]
    public async Task Ollama_Embed_ArrayInput_PreservedUpstream()
    {
        // Arrange
        MockUpstreamState.Handler = (_, _) =>
        {
            var body = """{"model":"nomic-embed-text","embeddings":[[0.1,0.2],[0.3,0.4]],"prompt_eval_count":5}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        };

        // Act: send an array input
        var payload = $$"""{"model":"{{EmbeddingModelName}}","input":["hello","world"]}""";
        var response = await Http.SendAsync(AuthedPost("/api/embed", payload));

        // Assert: upstream received the array input intact
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        Assert.IsNotNull(MockUpstreamState.LastRequestBody);
        var upstreamBody = JsonNode.Parse(MockUpstreamState.LastRequestBody);
        Assert.IsNotNull(upstreamBody);

        // The input must remain an array with 2 elements
        var inputNode = upstreamBody["input"]?.AsArray();
        Assert.IsNotNull(inputNode, "Upstream body 'input' must be an array");
        Assert.AreEqual(2, inputNode.Count);
        Assert.AreEqual("hello", inputNode[0]?.GetValue<string>());
        Assert.AreEqual("world", inputNode[1]?.GetValue<string>());
    }

    [TestMethod]
    public async Task Ollama_Embed_ResponseModelMasked()
    {
        // Arrange
        MockUpstreamState.Handler = (_, _) =>
        {
            var body = """{"model":"nomic-embed-text","embeddings":[[0.5,0.6,0.7]],"prompt_eval_count":2}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        };

        // Act
        var payload = $$"""{"model":"{{EmbeddingModelName}}","input":"test"}""";
        var response = await Http.SendAsync(AuthedPost("/api/embed", payload));

        // Assert: response model name is masked to virtual model name
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(content);
        Assert.IsNotNull(json);
        Assert.AreEqual(EmbeddingModelName, json["model"]?.ToString(),
            "Response must contain the virtual model name, not the physical one");
    }

    [TestMethod]
    public async Task OpenAI_NullRole_Handled()
    {
        // Arrange
        MockUpstreamState.Handler = (_, _) =>
        {
            var body = """{"model":"llama3.2","message":{"role":"assistant","content":"ok"},"done":true}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        };

        var payload = $$"""
        {
            "model": "{{VirtualModelName}}",
            "messages": [
                {
                    "content": "hi"
                }
            ],
            "stream": false
        }
        """;

        // Act
        var response = await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task OpenAI_ArrayInRole_Handled()
    {
        // Arrange
        MockUpstreamState.Handler = (_, _) =>
        {
            var body = """{"model":"llama3.2","message":{"role":"assistant","content":"ok"},"done":true}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        };

        var payload = $$"""
        {
            "model": "{{VirtualModelName}}",
            "messages": [
                {
                    "role": ["user"],
                    "content": "hi"
                }
            ],
            "stream": false
        }
        """;

        // Act
        var response = await Http.SendAsync(AuthedPost("/v1/chat/completions", payload));

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }
}
