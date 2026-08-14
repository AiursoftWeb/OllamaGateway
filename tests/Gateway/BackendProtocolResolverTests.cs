using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Gateway;

namespace Aiursoft.OllamaGateway.Tests.Gateway;

[TestClass]
public class BackendProtocolResolverTests
{
    [TestMethod]
    [DataRow(ProtocolDialect.OpenAiResponses, true, true, BackendProtocol.OpenAiResponses)]
    [DataRow(ProtocolDialect.OpenAiChatCompletions, true, true, BackendProtocol.OpenAiChatCompletions)]
    [DataRow(ProtocolDialect.OpenAiResponses, true, false, BackendProtocol.OpenAiChatCompletions)]
    [DataRow(ProtocolDialect.OpenAiChatCompletions, false, true, BackendProtocol.OpenAiResponses)]
    public void OpenAiProvider_SelectsMatchingProtocolOrTranslationFallback(
        ProtocolDialect clientDialect,
        bool supportsChat,
        bool supportsResponses,
        BackendProtocol expected)
    {
        var backend = Backend(supportsChat, supportsResponses);

        Assert.AreEqual(expected, BackendProtocolResolver.Resolve(backend, clientDialect));
    }

    [TestMethod]
    public void NonOpenAiClient_UsesConfiguredFallbackPreference()
    {
        var backend = Backend(supportsChat: true, supportsResponses: true);
        backend.Protocol = BackendProtocol.OpenAiResponses;

        Assert.AreEqual(
            BackendProtocol.OpenAiResponses,
            BackendProtocolResolver.Resolve(backend, ProtocolDialect.AnthropicMessages));
    }

    [TestMethod]
    public void OpenAiProviderWithoutGenerationProtocol_IsRejected()
    {
        var backend = Backend(supportsChat: false, supportsResponses: false);

        Assert.ThrowsExactly<InvalidOperationException>(() => BackendProtocolResolver.Resolve(backend));
    }

    [TestMethod]
    public void LegacyPersistedBackend_KeepsItsExistingResponsesProtocol()
    {
        var backend = new VirtualModelBackend
        {
            Id = 42,
            UnderlyingModelName = "physical",
            Protocol = BackendProtocol.OpenAiResponses,
            Provider = new OllamaProvider
            {
                Name = "Legacy Responses provider",
                BaseUrl = "https://legacy-responses.test",
                ProviderType = ProviderType.OpenAI
            }
        };

        Assert.AreEqual(
            BackendProtocol.OpenAiResponses,
            BackendProtocolResolver.Resolve(backend, ProtocolDialect.OpenAiChatCompletions));
    }

    [TestMethod]
    public void LegacyProvider_AggregatesProtocolsFromExistingBackendsForAdminDisplay()
    {
        var provider = new OllamaProvider
        {
            Name = "Legacy mixed provider",
            BaseUrl = "https://legacy-mixed.test",
            ProviderType = ProviderType.OpenAI,
            VirtualModelBackends =
            [
                new VirtualModelBackend
                {
                    UnderlyingModelName = "chat",
                    Protocol = BackendProtocol.OpenAiChatCompletions
                },
                new VirtualModelBackend
                {
                    UnderlyingModelName = "responses",
                    Protocol = BackendProtocol.OpenAiResponses
                }
            ]
        };

        var protocols = BackendProtocolResolver.GetProviderSupportedProtocols(provider);

        CollectionAssert.AreEquivalent(
            new[] { BackendProtocol.OpenAiChatCompletions, BackendProtocol.OpenAiResponses },
            protocols.ToArray());
    }

    [TestMethod]
    public void LegacyPhysicalBackend_UsesProtocolsInferredFromProviderBackends()
    {
        var provider = new OllamaProvider
        {
            Name = "Legacy Responses provider",
            BaseUrl = "https://legacy-physical.test",
            ProviderType = ProviderType.OpenAI,
            VirtualModelBackends =
            [
                new VirtualModelBackend
                {
                    Id = 7,
                    UnderlyingModelName = "responses",
                    Protocol = BackendProtocol.OpenAiResponses
                }
            ]
        };
        var physicalBackend = new VirtualModelBackend
        {
            UnderlyingModelName = "physical",
            Provider = provider
        };

        Assert.AreEqual(
            BackendProtocol.OpenAiResponses,
            BackendProtocolResolver.Resolve(physicalBackend, ProtocolDialect.OpenAiResponses));
    }

    private static VirtualModelBackend Backend(bool supportsChat, bool supportsResponses)
    {
        return new VirtualModelBackend
        {
            UnderlyingModelName = "physical",
            Provider = new OllamaProvider
            {
                Name = "OpenAI compatible",
                BaseUrl = "https://provider.test",
                ProviderType = ProviderType.OpenAI,
                SupportsOpenAiChatCompletions = supportsChat,
                SupportsOpenAiResponses = supportsResponses
            }
        };
    }
}
