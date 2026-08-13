using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Aiursoft.DbTools;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using static Aiursoft.WebTools.Extends;

namespace Aiursoft.OllamaGateway.Tests.IntegrationTests;

[TestClass]
public class OpenAIResponsesApiTests : TestBase
{
    private const string VirtualModelName = "responses-test:latest";
    private const string PhysicalModelName = "physical-model";

    [TestInitialize]
    public override async Task CreateServer()
    {
        TestStartup.MockClickhouse.Reset();
        TestStartup.MockClickhouse.Setup(client => client.Enabled).Returns(false);
        TestStartup.MockOllamaService.Reset();
        TestStartup.MockOllamaService
            .Setup(service => service.GetUnderlyingModelsAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync([PhysicalModelName]);
        MockUpstreamState.Reset();

        Server = await AppAsync<TestStartup>([], port: Port);
        await Server.UpdateDbAsync<TemplateDbContext>();
        await Server.SeedAsync();
        await Server.StartAsync();

        using var scope = Server.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<GlobalSettingsService>();
        await settings.UpdateSettingAsync(Configuration.SettingsMap.AllowAnonymousApiCall, "True");
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var staleModels = await db.VirtualModels
            .Include(model => model.VirtualModelBackends)
            .Where(model => model.Name == VirtualModelName)
            .ToListAsync();
        db.VirtualModels.RemoveRange(staleModels);
        await db.SaveChangesAsync();
        var provider = new OllamaProvider
        {
            Name = "Ollama",
            BaseUrl = "http://fake-ollama:11434",
            ProviderType = ProviderType.Ollama
        };
        db.OllamaProviders.Add(provider);
        await db.SaveChangesAsync();
        var model = new VirtualModel { Name = VirtualModelName, Type = ModelType.Chat };
        model.VirtualModelBackends.Add(new VirtualModelBackend
        {
            ProviderId = provider.Id,
            UnderlyingModelName = PhysicalModelName,
            Protocol = BackendProtocol.OllamaNative,
            Enabled = true,
            IsHealthy = true,
            Priority = 1,
            Weight = 1
        });
        db.VirtualModels.Add(model);
        await db.SaveChangesAsync();
    }

    [TestMethod]
    public async Task ResponsesToOllama_NonStreaming_UsesCanonicalTranslation()
    {
        MockUpstreamState.Handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"model":"physical-model","message":{"role":"assistant","content":"Hello"},"done":true,"prompt_eval_count":4,"eval_count":2}""",
                Encoding.UTF8,
                "application/json")
        });

        var response = await Http.PostAsync("/v1/responses", Json("""
        {"model":"responses-test:latest","instructions":"Be helpful","input":"Hi","stream":false,"store":false}
        """));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("/api/chat", MockUpstreamState.LastRequest?.RequestUri?.AbsolutePath);
        var upstream = JsonNode.Parse(MockUpstreamState.LastRequestBody!);
        Assert.AreEqual(PhysicalModelName, upstream?["model"]?.ToString());
        Assert.AreEqual("system", upstream?["messages"]?[0]?["role"]?.ToString());
        Assert.AreEqual("Be helpful", upstream?["messages"]?[0]?["content"]?.ToString());

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("response", body?["object"]?.ToString());
        Assert.AreEqual(VirtualModelName, body?["model"]?.ToString());
        Assert.AreEqual("Hello", body?["output"]?[0]?["content"]?[0]?["text"]?.ToString());
        Assert.AreEqual(6, body?["usage"]?["total_tokens"]?.GetValue<int>());
    }

    [TestMethod]
    public async Task ResponsesToOllama_Streaming_ReturnsTypedSse()
    {
        MockUpstreamState.Handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {"model":"physical-model","message":{"role":"assistant","content":"Hel"},"done":false}
                {"model":"physical-model","message":{"role":"assistant","content":"lo"},"done":true,"prompt_eval_count":2,"eval_count":1}
                """,
                Encoding.UTF8,
                "application/x-ndjson")
        });

        var response = await Http.PostAsync("/v1/responses", Json("""
        {"model":"responses-test:latest","input":"Hi","stream":true,"store":false}
        """));
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(body, "event: response.created");
        StringAssert.Contains(body, "\"type\":\"response.output_text.delta\"");
        StringAssert.Contains(body, "\"delta\":\"Hel\"");
        StringAssert.Contains(body, "\"delta\":\"lo\"");
        StringAssert.Contains(body, "event: response.completed");
    }

    [TestMethod]
    public async Task ResponsesToResponses_SameDialect_PreservesUnknownFieldsAndItems()
    {
        await ReplaceWithResponsesBackendAsync();
        MockUpstreamState.Handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {
              "id":"resp_upstream","object":"response","created_at":1,"status":"completed","model":"physical-responses",
              "output":[
                {"id":"ws_1","type":"web_search_call","status":"completed","vendor_extension":{"x":1}},
                {"id":"msg_1","type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","text":"Native","annotations":[]}]}
              ],
              "usage":{"input_tokens":3,"output_tokens":1,"total_tokens":4},
              "unknown_response_field":"kept"
            }
            """, Encoding.UTF8, "application/json")
        });

        var response = await Http.PostAsync("/v1/responses", Json("""
        {
          "model":"responses-test:latest","input":"Hi","store":false,
          "metadata":{"trace":"kept"},"unknown_request_field":{"x":1},
          "reasoning":{"effort":"high","summary":"detailed","future_option":"kept"},
          "text":{"format":{"type":"text"},"verbosity":"low","future_option":"kept"}
        }
        """));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("/v1/responses", MockUpstreamState.LastRequest?.RequestUri?.AbsolutePath);
        var upstream = JsonNode.Parse(MockUpstreamState.LastRequestBody!);
        Assert.AreEqual("kept", upstream?["metadata"]?["trace"]?.ToString());
        Assert.AreEqual(1, upstream?["unknown_request_field"]?["x"]?.GetValue<int>());
        Assert.AreEqual("detailed", upstream?["reasoning"]?["summary"]?.ToString());
        Assert.AreEqual("kept", upstream?["reasoning"]?["future_option"]?.ToString());
        Assert.AreEqual("low", upstream?["text"]?["verbosity"]?.ToString());
        Assert.AreEqual("kept", upstream?["text"]?["future_option"]?.ToString());
        Assert.AreEqual("physical-responses", upstream?["model"]?.ToString());
        Assert.AreEqual(false, upstream?["store"]?.GetValue<bool>());

        var result = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(VirtualModelName, result?["model"]?.ToString());
        Assert.AreEqual("web_search_call", result?["output"]?[0]?["type"]?.ToString());
        Assert.AreEqual(1, result?["output"]?[0]?["vendor_extension"]?["x"]?.GetValue<int>());
        Assert.AreEqual("kept", result?["unknown_response_field"]?.ToString());
    }

    [TestMethod]
    public async Task ResponsesNativeTool_SelectsResponsesCapableBackend()
    {
        await AddResponsesBackendAsync(priority: 10);
        MockUpstreamState.Handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {"id":"resp_1","object":"response","created_at":1,"status":"completed","model":"physical-responses","output":[],"usage":{"input_tokens":1,"output_tokens":0,"total_tokens":1}}
            """, Encoding.UTF8, "application/json")
        });

        var response = await Http.PostAsync("/v1/responses", Json("""
        {"model":"responses-test:latest","input":"search","store":false,"tools":[{"type":"web_search"}]}
        """));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("/v1/responses", MockUpstreamState.LastRequest?.RequestUri?.AbsolutePath);
    }

    [TestMethod]
    public async Task ResponsesToResponses_Streaming_PreservesUnknownEvents()
    {
        await ReplaceWithResponsesBackendAsync();
        MockUpstreamState.Handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            event: response.created
            data: {"type":"response.created","response":{"id":"resp_1","object":"response","status":"in_progress","model":"physical-responses","output":[]}}

            event: response.future_event
            data: {"type":"response.future_event","future_payload":{"kept":true}}

            event: response.completed
            data: {"type":"response.completed","response":{"id":"resp_1","object":"response","status":"completed","model":"physical-responses","output":[],"usage":{"input_tokens":1,"output_tokens":0,"total_tokens":1}}}

            """, Encoding.UTF8, "text/event-stream")
        });

        var response = await Http.PostAsync("/v1/responses", Json("""
        {"model":"responses-test:latest","input":"Hi","stream":true,"store":false,"future_request_field":42}
        """));
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(body, "event: response.future_event");
        StringAssert.Contains(body, "\"future_payload\":{\"kept\":true}");
        StringAssert.Contains(body, $"\"model\":\"{VirtualModelName}\"");
        var upstream = JsonNode.Parse(MockUpstreamState.LastRequestBody!);
        Assert.AreEqual(42, upstream?["future_request_field"]?.GetValue<int>());
    }

    [TestMethod]
    public async Task ResponsesNativeTool_WithoutCapableBackend_ReturnsExplicitError()
    {
        var response = await Http.PostAsync("/v1/responses", Json("""
        {"model":"responses-test:latest","input":"search","store":false,"tools":[{"type":"web_search"}]}
        """));
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual("unsupported_feature", body?["error"]?["code"]?.ToString());
        Assert.IsNull(MockUpstreamState.LastRequest);
    }

    [TestMethod]
    public async Task ResponsesStatefulFields_ReturnExplicitError()
    {
        var response = await Http.PostAsync("/v1/responses", Json("""
        {"model":"responses-test:latest","input":"continue","previous_response_id":"resp_old","store":false}
        """));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "stateless");
        Assert.IsNull(MockUpstreamState.LastRequest);
    }

    [TestMethod]
    public async Task ChatCompletionsToResponsesBackend_TranslatesBothDirections()
    {
        await ReplaceWithResponsesBackendAsync();
        MockUpstreamState.Handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {
              "id":"resp_1","object":"response","created_at":1,"status":"completed","model":"physical-responses",
              "output":[{"id":"msg_1","type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","text":"Translated","annotations":[]}]}],
              "usage":{"input_tokens":2,"output_tokens":1,"total_tokens":3}
            }
            """, Encoding.UTF8, "application/json")
        });

        var response = await Http.PostAsync("/v1/chat/completions", Json("""
        {
          "model":"responses-test:latest","messages":[{"role":"user","content":"Hi"}],"stream":false,
          "tools":[{"type":"function","function":{"name":"lookup","parameters":{"type":"object"}}}],
          "tool_choice":{"type":"function","function":{"name":"lookup"}}
        }
        """));
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("/v1/responses", MockUpstreamState.LastRequest?.RequestUri?.AbsolutePath);
        var upstream = JsonNode.Parse(MockUpstreamState.LastRequestBody!);
        Assert.AreEqual("lookup", upstream?["tools"]?[0]?["name"]?.ToString());
        Assert.AreEqual("lookup", upstream?["tool_choice"]?["name"]?.ToString());
        Assert.IsNull(upstream?["tool_choice"]?["function"]);
        Assert.AreEqual("Translated", body?["choices"]?[0]?["message"]?["content"]?.ToString());
        Assert.AreEqual(VirtualModelName, body?["model"]?.ToString());
    }

    private async Task ReplaceWithResponsesBackendAsync()
    {
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var model = await db.VirtualModels.Include(item => item.VirtualModelBackends).SingleAsync(item => item.Name == VirtualModelName);
        db.VirtualModelBackends.RemoveRange(model.VirtualModelBackends);
        await db.SaveChangesAsync();
        await AddResponsesBackendAsync();
    }

    private async Task AddResponsesBackendAsync(int priority = 1)
    {
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var model = await db.VirtualModels.SingleAsync(item => item.Name == VirtualModelName);
        var provider = new OllamaProvider
        {
            Name = $"OpenAI Responses {Guid.NewGuid():N}",
            BaseUrl = "http://fake-openai.test",
            ProviderType = ProviderType.OpenAI
        };
        db.OllamaProviders.Add(provider);
        await db.SaveChangesAsync();
        db.VirtualModelBackends.Add(new VirtualModelBackend
        {
            VirtualModelId = model.Id,
            ProviderId = provider.Id,
            UnderlyingModelName = "physical-responses",
            Protocol = BackendProtocol.OpenAiResponses,
            Enabled = true,
            IsHealthy = true,
            Priority = priority,
            Weight = 1
        });
        await db.SaveChangesAsync();
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
}
