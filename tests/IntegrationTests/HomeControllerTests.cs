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
    public async Task CompatibilityMatrix_MatchesSemanticIrCapabilities()
    {
        var response = await Http.GetAsync("/");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("data-compatibility-inbound-formats=\"3\"", html);
        Assert.Contains("data-compatibility-matrix=\"semantic-ir-v1\"", html);
        Assert.Contains("data-capability=\"ollama-openai-top-k\" data-support=\"not-supported\"", html);
        Assert.Contains("data-capability=\"ollama-openai-thinking\" data-support=\"converted\"", html);
        Assert.Contains("data-capability=\"anthropic-openai-images\" data-support=\"base64-url-converted\"", html);
        Assert.Contains("data-capability=\"anthropic-ollama-images\" data-support=\"base64-converted\"", html);
        Assert.DoesNotContain("Anthropic image content blocks are not yet translated", html);
        Assert.DoesNotContain("DB override discarded", html);
    }
}
