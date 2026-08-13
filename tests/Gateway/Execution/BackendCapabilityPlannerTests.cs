using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Gateway.Execution;

namespace Aiursoft.OllamaGateway.Tests.Gateway.Execution;

[TestClass]
public class BackendCapabilityPlannerTests
{
    private readonly BackendCapabilityPlanner _planner = new();

    [TestMethod]
    public void OllamaBackend_SupportsAllCurrentCapabilities()
    {
        var backend = CreateBackend(ProviderType.Ollama);

        Assert.IsTrue(_planner.Supports(backend, GatewayCapability.ChatCompletion));
        Assert.IsTrue(_planner.Supports(backend, GatewayCapability.TextGeneration));
        Assert.IsTrue(_planner.Supports(backend, GatewayCapability.Embedding));
    }

    [TestMethod]
    public void OpenAiBackend_DoesNotSupportOllamaTextGeneration()
    {
        var backend = CreateBackend(ProviderType.OpenAI);

        Assert.IsTrue(_planner.Supports(backend, GatewayCapability.ChatCompletion));
        Assert.IsTrue(_planner.Supports(backend, GatewayCapability.Embedding));
        Assert.IsFalse(_planner.Supports(backend, GatewayCapability.TextGeneration));
    }

    [TestMethod]
    public void BackendWithoutProvider_IsNotEligible()
    {
        var backend = new VirtualModelBackend { UnderlyingModelName = "model" };

        Assert.IsFalse(_planner.Supports(backend, GatewayCapability.ChatCompletion));
    }

    [TestMethod]
    public void ResponsesBackend_SupportsNativeTools_ChatBackendDoesNot()
    {
        var responses = CreateBackend(ProviderType.OpenAI);
        responses.Protocol = BackendProtocol.OpenAiResponses;
        var chat = CreateBackend(ProviderType.OpenAI);
        chat.Protocol = BackendProtocol.OpenAiChatCompletions;

        var requested = GatewayCapability.ChatCompletion |
                        GatewayCapability.Streaming |
                        GatewayCapability.NativeTools;
        Assert.IsTrue(_planner.Supports(responses, requested));
        Assert.IsFalse(_planner.Supports(chat, requested));
    }

    [TestMethod]
    public void ResponsesBackend_DoesNotAdvertiseStatefulResponses()
    {
        var responses = CreateBackend(ProviderType.OpenAI);
        responses.Protocol = BackendProtocol.OpenAiResponses;

        Assert.IsFalse(_planner.Supports(responses, GatewayCapability.StatefulResponses));
    }

    [TestMethod]
    public void LegacyBackend_InfersProtocolFromProviderType()
    {
        var openAi = CreateBackend(ProviderType.OpenAI);
        var ollama = CreateBackend(ProviderType.Ollama);

        Assert.IsTrue(_planner.Supports(openAi, GatewayCapability.ChatCompletion));
        Assert.IsFalse(_planner.Supports(openAi, GatewayCapability.TextGeneration));
        Assert.IsTrue(_planner.Supports(ollama, GatewayCapability.TextGeneration));
    }

    private static VirtualModelBackend CreateBackend(ProviderType providerType)
    {
        return new VirtualModelBackend
        {
            UnderlyingModelName = "model",
            Provider = new OllamaProvider
            {
                Name = "provider",
                BaseUrl = "http://provider.test",
                ProviderType = providerType
            }
        };
    }
}
