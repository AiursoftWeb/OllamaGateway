namespace Aiursoft.OllamaGateway.Tests.IntegrationTests;

[TestClass]
public class HomeControllerTests : TestBase
{
    [TestMethod]
    public async Task GetIndex()
    {
        var url = "/";
        var response = await Http.GetAsync(url);
        response.EnsureSuccessStatusCode();
    }

    [TestMethod]
    public async Task CompatibilityMatrix_DocumentsEverySemanticRoute()
    {
        var response = await Http.GetAsync("/");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("data-compatibility-inbound-formats=\"4\"", html);
        Assert.Contains("data-compatibility-backend-protocols=\"3\"", html);
        Assert.Contains("data-compatibility-matrix=\"semantic-ir-v2\"", html);
        Assert.Contains("data-translation-directions=\"12\"", html);
        Assert.Contains("id=\"compatibility-chat-tab\"", html);
        Assert.Contains("id=\"compatibility-embedding-tab\"", html);
        Assert.Contains("compatibility-workbench", html);
        Assert.Contains("data-compatibility-source=\"openai-responses\"", html);
        Assert.Contains("data-compatibility-target=\"openai-responses\"", html);
        Assert.Contains("data-active-route-contract", html);
        Assert.Contains("data-route-catalog", html);

        string[] directions =
        [
            "ollama-to-ollama",
            "ollama-to-openai-chat",
            "ollama-to-openai-responses",
            "openai-chat-to-ollama",
            "openai-chat-to-openai-chat",
            "openai-chat-to-openai-responses",
            "openai-responses-to-ollama",
            "openai-responses-to-openai-chat",
            "openai-responses-to-openai-responses",
            "anthropic-to-ollama",
            "anthropic-to-openai-chat",
            "anthropic-to-openai-responses"
        ];
        foreach (var direction in directions)
        {
            Assert.Contains($"data-direction=\"{direction}\"", html);
        }

        Assert.Contains("data-db-override-policy=\"hard-assignment\"", html);
        Assert.Contains("data-responses-state=\"native-only\"", html);
        Assert.Contains("data-auxiliary-api-contract=\"embedding-and-ollama-operations\"", html);
        Assert.Contains("data-embedding-inspector", html);
        Assert.Contains("data-embedding-source=\"openai-embedding\"", html);
        Assert.Contains("data-embedding-target=\"ollama\"", html);
        Assert.Contains("data-aux-route=\"openai-embedding-to-ollama\"", html);
        Assert.Contains("data-aux-route=\"ollama-embedding-to-openai\"", html);
        Assert.Contains("data-aux-route=\"ollama-generate\"", html);
        Assert.Contains("data-aux-route=\"model-discovery\"", html);
        Assert.Contains("OpenAiResponsesWireResponseWriter", html);
        Assert.Contains("GatewayChatRequest", html);
        Assert.Contains("previous_response_id", html);
        Assert.DoesNotContain("Universal API Compatibility", html);
        Assert.DoesNotContain("in any combination", html);
        Assert.DoesNotContain("min-width: 1400px", html);
        Assert.DoesNotContain("id=\"auxiliaryApiContract\"", html);
    }
}
