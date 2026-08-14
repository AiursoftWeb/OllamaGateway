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
public class OpenAIProtocolNegotiationTests : TestBase
{
    private const string PhysicalModel = "gpt-physical";
    private const string ChatOnlyModel = "dialect-chat-only:latest";
    private const string ResponsesOnlyModel = "dialect-responses-only:latest";
    private const string BothModel = "dialect-both:latest";
    private const string PreferenceModel = "dialect-preference:latest";

    [TestInitialize]
    public override async Task CreateServer()
    {
        TestStartup.MockClickhouse.Reset();
        TestStartup.MockClickhouse.Setup(client => client.Enabled).Returns(false);
        TestStartup.MockOllamaService.Reset();
        TestStartup.MockOllamaService
            .Setup(service => service.GetOpenAIAvailableModelsAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync([PhysicalModel]);
        MockUpstreamState.Reset();

        Server = await AppAsync<TestStartup>([], port: Port);
        await Server.UpdateDbAsync<TemplateDbContext>();
        await Server.SeedAsync();
        await Server.StartAsync();

        using var scope = Server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<GlobalSettingsService>();
        await settings.UpdateSettingAsync(Configuration.SettingsMap.AllowAnonymousApiCall, "True");

        var names = new[] { ChatOnlyModel, ResponsesOnlyModel, BothModel, PreferenceModel };
        var stale = await db.VirtualModels
            .Include(model => model.VirtualModelBackends)
            .Where(model => names.Contains(model.Name))
            .ToListAsync();
        db.VirtualModels.RemoveRange(stale);
        await db.SaveChangesAsync();

        var chatProvider = Provider("Chat only", "chat-only", supportsChat: true, supportsResponses: false);
        var responsesProvider = Provider("Responses only", "responses-only", supportsChat: false, supportsResponses: true);
        var bothProvider = Provider("Both protocols", "both", supportsChat: true, supportsResponses: true);
        db.OllamaProviders.AddRange(chatProvider, responsesProvider, bothProvider);
        await db.SaveChangesAsync();

        AddModel(db, ChatOnlyModel, (chatProvider, 1));
        AddModel(db, ResponsesOnlyModel, (responsesProvider, 1));
        AddModel(db, BothModel, (bothProvider, 1));
        AddModel(db, PreferenceModel, (chatProvider, 0), (responsesProvider, 10));
        await db.SaveChangesAsync();
    }

    [TestMethod]
    public async Task NewToNew_UsesResponsesDirectlyAndPreservesUnknownFields()
    {
        MockUpstreamState.Handler = (_, _) => Task.FromResult(ResponsesResponse("native new", includeExtension: true));

        var response = await Http.PostAsync("/v1/responses", Json(
            $$$"""{"model":"{{{BothModel}}}","input":"hello","store":false,"vendor_request":{"preserved":true}}"""));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("/v1/responses", MockUpstreamState.LastRequest?.RequestUri?.AbsolutePath);
        var upstream = JsonNode.Parse(MockUpstreamState.LastRequestBody!);
        Assert.AreEqual(true, upstream?["vendor_request"]?["preserved"]?.GetValue<bool>());
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(BothModel, body?["model"]?.ToString());
        Assert.AreEqual(true, body?["vendor_extension"]?["preserved"]?.GetValue<bool>());
    }

    [TestMethod]
    public async Task NewToOld_UsesChatTranslationAndReturnsResponsesShape()
    {
        MockUpstreamState.Handler = (_, _) => Task.FromResult(ChatResponse("translated to old"));

        var response = await Http.PostAsync("/v1/responses", Json(
            $$$"""{"model":"{{{ChatOnlyModel}}}","instructions":"be helpful","input":"hello","store":false}"""));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("/v1/chat/completions", MockUpstreamState.LastRequest?.RequestUri?.AbsolutePath);
        var upstream = JsonNode.Parse(MockUpstreamState.LastRequestBody!);
        Assert.AreEqual("system", upstream?["messages"]?[0]?["role"]?.ToString());
        Assert.AreEqual("hello", upstream?["messages"]?[1]?["content"]?.ToString());
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("response", body?["object"]?.ToString());
        Assert.AreEqual("translated to old", body?["output"]?[0]?["content"]?[0]?["text"]?.ToString());
    }

    [TestMethod]
    public async Task OldToOld_UsesChatDirectlyAndPreservesUnknownFields()
    {
        MockUpstreamState.Handler = (_, _) => Task.FromResult(ChatResponse("native old", includeExtension: true));

        var response = await Http.PostAsync("/v1/chat/completions", Json(
            $$$"""{"model":"{{{BothModel}}}","messages":[{"role":"user","content":"hello"}],"vendor_request":{"preserved":true}}"""));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("/v1/chat/completions", MockUpstreamState.LastRequest?.RequestUri?.AbsolutePath);
        var upstream = JsonNode.Parse(MockUpstreamState.LastRequestBody!);
        Assert.AreEqual(true, upstream?["vendor_request"]?["preserved"]?.GetValue<bool>());
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(BothModel, body?["model"]?.ToString());
        Assert.AreEqual(true, body?["vendor_extension"]?["preserved"]?.GetValue<bool>());
    }

    [TestMethod]
    public async Task OldToNew_UsesResponsesTranslationAndReturnsChatShape()
    {
        MockUpstreamState.Handler = (_, _) => Task.FromResult(ResponsesResponse("translated to new"));

        var response = await Http.PostAsync("/v1/chat/completions", Json(
            $$$"""{"model":"{{{ResponsesOnlyModel}}}","messages":[{"role":"user","content":"hello"}]}"""));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("/v1/responses", MockUpstreamState.LastRequest?.RequestUri?.AbsolutePath);
        var upstream = JsonNode.Parse(MockUpstreamState.LastRequestBody!);
        Assert.AreEqual("hello", upstream?["input"]?[0]?["content"]?.ToString());
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("translated to new", body?["choices"]?[0]?["message"]?["content"]?.ToString());
    }

    [TestMethod]
    public async Task MatchingDialect_IsPreferredOverHigherPriorityTranslationBackend()
    {
        MockUpstreamState.Handler = (_, _) => Task.FromResult(ResponsesResponse("preferred native"));

        var response = await Http.PostAsync("/v1/responses", Json(
            $$$"""{"model":"{{{PreferenceModel}}}","input":"hello","store":false}"""));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("/v1/responses", MockUpstreamState.LastRequest?.RequestUri?.AbsolutePath);
        Assert.AreEqual("responses-only.test", MockUpstreamState.LastRequest?.RequestUri?.Host);
    }

    [TestMethod]
    public async Task StatefulResponses_AreForwardedOnlyToNativeResponsesBackend()
    {
        MockUpstreamState.Handler = (_, _) => Task.FromResult(ResponsesResponse("continued"));
        var native = await Http.PostAsync("/v1/responses", Json(
            $$$"""{"model":"{{{ResponsesOnlyModel}}}","input":"continue","previous_response_id":"resp_previous","store":true}"""));

        Assert.AreEqual(HttpStatusCode.OK, native.StatusCode);
        Assert.AreEqual("/v1/responses", MockUpstreamState.LastRequest?.RequestUri?.AbsolutePath);
        var upstream = JsonNode.Parse(MockUpstreamState.LastRequestBody!);
        Assert.AreEqual("resp_previous", upstream?["previous_response_id"]?.ToString());
        Assert.AreEqual(true, upstream?["store"]?.GetValue<bool>());

        MockUpstreamState.Reset();
        var translated = await Http.PostAsync("/v1/responses", Json(
            $$$"""{"model":"{{{ChatOnlyModel}}}","input":"continue","previous_response_id":"resp_previous","store":true}"""));
        Assert.AreEqual(HttpStatusCode.BadRequest, translated.StatusCode);
        StringAssert.Contains(await translated.Content.ReadAsStringAsync(), "unsupported_feature");
        Assert.IsNull(MockUpstreamState.LastRequest);
    }

    private static OllamaProvider Provider(
        string name,
        string host,
        bool supportsChat,
        bool supportsResponses)
    {
        return new OllamaProvider
        {
            Name = name,
            BaseUrl = $"https://{host}.test",
            ProviderType = ProviderType.OpenAI,
            SupportsOpenAiChatCompletions = supportsChat,
            SupportsOpenAiResponses = supportsResponses
        };
    }

    private static void AddModel(
        TemplateDbContext db,
        string name,
        params (OllamaProvider Provider, int Priority)[] backends)
    {
        var model = new VirtualModel
        {
            Name = name,
            Type = ModelType.Chat,
            SelectionStrategy = SelectionStrategy.PriorityFallback
        };
        foreach (var (provider, priority) in backends)
        {
            model.VirtualModelBackends.Add(new VirtualModelBackend
            {
                ProviderId = provider.Id,
                UnderlyingModelName = PhysicalModel,
                Protocol = provider.SupportsOpenAiResponses == true && provider.SupportsOpenAiChatCompletions != true
                    ? BackendProtocol.OpenAiResponses
                    : BackendProtocol.OpenAiChatCompletions,
                Priority = priority,
                Weight = 1,
                Enabled = true,
                IsHealthy = true
            });
        }
        db.VirtualModels.Add(model);
    }

    private static HttpResponseMessage ChatResponse(string text, bool includeExtension = false)
    {
        var extension = includeExtension ? ",\"vendor_extension\":{\"preserved\":true}" : string.Empty;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"{{\"id\":\"chatcmpl_1\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"{PhysicalModel}\",\"choices\":[{{\"index\":0,\"message\":{{\"role\":\"assistant\",\"content\":\"{text}\"}},\"finish_reason\":\"stop\"}}],\"usage\":{{\"prompt_tokens\":2,\"completion_tokens\":1,\"total_tokens\":3}}{extension}}}",
                Encoding.UTF8,
                "application/json")
        };
    }

    private static HttpResponseMessage ResponsesResponse(string text, bool includeExtension = false)
    {
        var extension = includeExtension ? ",\"vendor_extension\":{\"preserved\":true}" : string.Empty;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"{{\"id\":\"resp_1\",\"object\":\"response\",\"created_at\":1,\"status\":\"completed\",\"model\":\"{PhysicalModel}\",\"output\":[{{\"id\":\"msg_1\",\"type\":\"message\",\"status\":\"completed\",\"role\":\"assistant\",\"content\":[{{\"type\":\"output_text\",\"text\":\"{text}\",\"annotations\":[]}}]}}],\"usage\":{{\"input_tokens\":2,\"output_tokens\":1,\"total_tokens\":3}}{extension}}}",
                Encoding.UTF8,
                "application/json")
        };
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
}
