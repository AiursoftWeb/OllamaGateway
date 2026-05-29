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
/// Tests that reasoning_content in thinking mode properly flows through the gateway:
/// 1. Backend reasoning_content → Anthropic response thinking blocks
/// 2. Anthropic request thinking blocks → backend reasoning_content
/// 3. Multi-turn round-trip preserves reasoning_content
/// </summary>
[TestClass]
public class AnthropicReasoningTests : TestBase
{
    private const string TestApiKey = "reasoning-test-key";
    private const string VirtualModelName = "thinking-model";
    private const string PhysicalModelName = "claude-sonnet-4-6";

    [TestInitialize]
    public override async Task CreateServer()
    {
        TestStartup.MockClickhouse.Reset();
        TestStartup.MockClickhouse.Setup(c => c.Enabled).Returns(false);

        TestStartup.MockOllamaService.Reset();
        TestStartup.MockOllamaService.Setup(s => s.GetUnderlyingModelsAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<string> { PhysicalModelName });
        TestStartup.MockOllamaService.Setup(s => s.GetDetailedModelsAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<OllamaService.OllamaModel>
            {
                new() { Name = PhysicalModelName, Size = 1024 * 1024 * 1024L }
            });

        MockUpstreamState.Reset();

        Server = await AppAsync<TestStartup>([], port: Port);
        await Server.UpdateDbAsync<TemplateDbContext>();
        await Server.SeedAsync();
        await Server.StartAsync();

        // Seed database: provider + virtual model + API key
        using var scope = Server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<GlobalSettingsService>();
        await settings.UpdateSettingAsync(Configuration.SettingsMap.AllowAnonymousApiCall, "True");

        var provider = new OllamaProvider
        {
            Name = "ThinkingProvider",
            BaseUrl = "http://fake-backend:8080",
            ProviderType = ProviderType.OpenAI // Use OpenAI provider type for Anthropic→OpenAI translation
        };
        db.OllamaProviders.Add(provider);
        await db.SaveChangesAsync();

        var vm = new VirtualModel
        {
            Name = VirtualModelName,
            Type = ModelType.Chat,
            MaxRetries = 1,
            Thinking = true
        };
        db.VirtualModels.Add(vm);
        await db.SaveChangesAsync();

        db.VirtualModelBackends.Add(new VirtualModelBackend
        {
            VirtualModelId = vm.Id,
            ProviderId = provider.Id,
            UnderlyingModelName = PhysicalModelName,
            Enabled = true,
            IsHealthy = true
        });
        await db.SaveChangesAsync();

        // Create API key
        var userInDb = await db.Users.FirstAsync();
        db.ApiKeys.Add(new ApiKey
        {
            Name = "Reasoning Test Key",
            Key = TestApiKey,
            UserId = userInDb.Id,
            ExpirationTime = DateTime.UtcNow.AddDays(1)
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Test: When the backend returns reasoning_content in the OpenAI-format response,
    /// the gateway SHOULD include thinking blocks in the Anthropic-format response.
    ///
    /// This test is expected to FAIL initially because the AnthropicController does not
    /// capture reasoning_content from the backend response nor include thinking blocks.
    /// </summary>
    [TestMethod]
    public async Task BackendReasoning_ShouldAppearInAnthropicResponse()
    {
        // Arrange: Mock backend to return reasoning_content in OpenAI format
        MockUpstreamState.Handler = (req, _) =>
        {
            var backendResponse = new JsonObject
            {
                ["id"] = "chatcmpl-123",
                ["object"] = "chat.completion",
                ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["model"] = PhysicalModelName,
                ["choices"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["index"] = 0,
                        ["message"] = new JsonObject
                        {
                            ["role"] = "assistant",
                            ["content"] = "The answer is 42.",
                            ["reasoning_content"] = "Let me think about this step by step. First, I need to understand the question..."
                        },
                        ["finish_reason"] = "stop"
                    }
                },
                ["usage"] = new JsonObject
                {
                    ["prompt_tokens"] = 50,
                    ["completion_tokens"] = 30,
                    ["total_tokens"] = 80
                }
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(backendResponse.ToJsonString(), Encoding.UTF8, "application/json")
            });
        };

        // Act: Send Anthropic-format request
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent.Create(new
            {
                model = VirtualModelName,
                max_tokens = 1024,
                messages = new[]
                {
                    new { role = "user", content = "What is the answer?" }
                }
            })
        };
        request.Headers.Add("x-api-key", TestApiKey);

        var response = await Http.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var responseBody = await response.Content.ReadAsStringAsync();

        var responseJson = JsonNode.Parse(responseBody);

        Assert.IsNotNull(responseJson, "Response should be valid JSON");

        // Verify: content array should include a thinking block
        var contentArray = responseJson["content"]?.AsArray();
        Assert.IsNotNull(contentArray, "Response should have a content array");

        var thinkingBlock = contentArray!
            .FirstOrDefault(c => c?["type"]?.ToString() == "thinking");
        Assert.IsNotNull(thinkingBlock,
            "Response should include a thinking content block with the reasoning_content from the backend");

        var thinkingText = thinkingBlock!["thinking"]?.ToString();
        Assert.IsNotNull(thinkingText, "Thinking block should have thinking text");
        StringAssert.Contains(thinkingText, "step by step",
            "Thinking content should match the backend's reasoning_content");

        // Signature is required by the official Anthropic API for multi-turn continuity.
        // The gateway generates an opaque token since the backend does not provide one.
        var signature = thinkingBlock!["signature"]?.ToString();
        Assert.IsFalse(string.IsNullOrEmpty(signature),
            "Thinking block should have a non-empty signature for Anthropic API compatibility");

        // Verify: text content block should still be present
        var textBlock = contentArray
            .FirstOrDefault(c => c?["type"]?.ToString() == "text");
        Assert.IsNotNull(textBlock, "Response should still include the text content block");
        Assert.AreEqual("The answer is 42.", textBlock!["text"]?.ToString());
    }

    /// <summary>
    /// Test: When the client sends thinking blocks in the request (proper Anthropic format),
    /// the gateway SHOULD translate them to reasoning_content for the OpenAI-format backend.
    ///
    /// This test verifies the request direction: Anthropic thinking blocks → OpenAI reasoning_content.
    /// </summary>
    [TestMethod]
    public async Task ThinkingBlocks_ShouldBecomeReasoningContent_ForBackend()
    {
        MockUpstreamState.Handler = (req, _) =>
        {
            var backendResponse = new JsonObject
            {
                ["id"] = "chatcmpl-456",
                ["object"] = "chat.completion",
                ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["model"] = PhysicalModelName,
                ["choices"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["index"] = 0,
                        ["message"] = new JsonObject
                        {
                            ["role"] = "assistant",
                            ["content"] = "Here is the solution."
                        },
                        ["finish_reason"] = "stop"
                    }
                },
                ["usage"] = new JsonObject
                {
                    ["prompt_tokens"] = 40,
                    ["completion_tokens"] = 20,
                    ["total_tokens"] = 60
                }
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(backendResponse.ToJsonString(), Encoding.UTF8, "application/json")
            });
        };

        // Act: Send Anthropic-format request with thinking blocks in assistant message history
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent.Create(new
            {
                model = VirtualModelName,
                max_tokens = 1024,
                messages = new object[]
                {
                    new
                    {
                        role = "user",
                        content = new[] { new { type = "text", text = "Solve this puzzle." } }
                    },
                    new
                    {
                        role = "assistant",
                        content = new object[]
                        {
                            new { type = "thinking", thinking = "I need to analyze this puzzle carefully." },
                            new { type = "text", text = "Let me solve it." }
                        }
                    },
                    new
                    {
                        role = "user",
                        content = new[] { new { type = "text", text = "Explain step 2." } }
                    }
                }
            })
        };
        request.Headers.Add("x-api-key", TestApiKey);

        var response = await Http.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        // Verify: The upstream request to backend should include reasoning_content on the assistant message
        var upstreamBody = JsonNode.Parse(MockUpstreamState.LastRequestBody ?? "{}");
        Assert.IsNotNull(upstreamBody, "Upstream body should exist");

        var messages = upstreamBody!["messages"]?.AsArray();
        Assert.IsNotNull(messages, "Upstream request should have messages array");

        // Find the assistant message (index 1)
        var assistantMsg = messages!.ElementAtOrDefault(1);
        Assert.IsNotNull(assistantMsg, "Second message should be the assistant message");

        var reasoningContent = assistantMsg!["reasoning_content"]?.ToString();
        Assert.IsNotNull(reasoningContent,
            "Upstream assistant message should have reasoning_content from the thinking block");
        Assert.AreEqual("I need to analyze this puzzle carefully.", reasoningContent);
    }

    /// <summary>
    /// Test: Multi-turn round-trip: the full flow from backend reasoning → Anthropic response thinking →
    /// client sends thinking blocks back → backend gets reasoning_content.
    ///
    /// This tests the complete reasoning_content preservation through the gateway.
    /// </summary>
    [TestMethod]
    public async Task MultiTurn_RoundTrip_PreservesReasoning()
    {
        // First call: backend returns reasoning_content
        MockUpstreamState.Handler = (req, _) =>
        {
            var backendResponse = new JsonObject
            {
                ["id"] = "chatcmpl-round1",
                ["object"] = "chat.completion",
                ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["model"] = PhysicalModelName,
                ["choices"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["index"] = 0,
                        ["message"] = new JsonObject
                        {
                            ["role"] = "assistant",
                            ["content"] = "Here is my analysis.",
                            ["reasoning_content"] = "Analyzing the problem from multiple angles..."
                        },
                        ["finish_reason"] = "stop"
                    }
                },
                ["usage"] = new JsonObject
                {
                    ["prompt_tokens"] = 30,
                    ["completion_tokens"] = 15,
                    ["total_tokens"] = 45
                }
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(backendResponse.ToJsonString(), Encoding.UTF8, "application/json")
            });
        };

        // First request: simple user message
        var firstRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent.Create(new
            {
                model = VirtualModelName,
                max_tokens = 1024,
                messages = new[]
                {
                    new { role = "user", content = "Analyze this problem." }
                }
            })
        };
        firstRequest.Headers.Add("x-api-key", TestApiKey);

        var firstResponse = await Http.SendAsync(firstRequest);
        Assert.AreEqual(HttpStatusCode.OK, firstResponse.StatusCode);
        var firstResponseBody = await firstResponse.Content.ReadAsStringAsync();
        var firstResponseJson = JsonNode.Parse(firstResponseBody);

        // Extract the assistant message content from the first response
        var assistantContent = firstResponseJson?["content"]?.AsArray();
        Assert.IsNotNull(assistantContent, "First response should have content array");

        // Verify it includes a thinking block
        var thinkingBlock = assistantContent!
            .FirstOrDefault(c => c?["type"]?.ToString() == "thinking");
        Assert.IsNotNull(thinkingBlock,
            "First response should include thinking block from backend reasoning_content");

        // Now make a second request using the thinking blocks from the first response
        // This simulates a multi-turn conversation where the client echoes the thinking
        MockUpstreamState.Reset();
        MockUpstreamState.Handler = (req, _) =>
        {
            var backendResponse = new JsonObject
            {
                ["id"] = "chatcmpl-round2",
                ["object"] = "chat.completion",
                ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["model"] = PhysicalModelName,
                ["choices"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["index"] = 0,
                        ["message"] = new JsonObject
                        {
                            ["role"] = "assistant",
                            ["content"] = "Sure, let me explain further."
                        },
                        ["finish_reason"] = "stop"
                    }
                },
                ["usage"] = new JsonObject
                {
                    ["prompt_tokens"] = 60,
                    ["completion_tokens"] = 20,
                    ["total_tokens"] = 80
                }
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(backendResponse.ToJsonString(), Encoding.UTF8, "application/json")
            });
        };

        var secondRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent.Create(new
            {
                model = VirtualModelName,
                max_tokens = 1024,
                messages = new object[]
                {
                    new
                    {
                        role = "user",
                        content = new[] { new { type = "text", text = "Analyze this problem." } }
                    },
                    // This is the assistant response from the first turn, with thinking blocks
                    new
                    {
                        role = "assistant",
                        content = assistantContent
                    },
                    new
                    {
                        role = "user",
                        content = new[] { new { type = "text", text = "Explain further." } }
                    }
                }
            })
        };
        secondRequest.Headers.Add("x-api-key", TestApiKey);

        var secondResponse = await Http.SendAsync(secondRequest);
        Assert.AreEqual(HttpStatusCode.OK, secondResponse.StatusCode,
            "Second request should succeed — reasoning_content should be preserved in the round-trip");

        // Verify: the upstream request for the second call should have reasoning_content
        var upstreamBody = JsonNode.Parse(MockUpstreamState.LastRequestBody ?? "{}");
        var upstreamMessages = upstreamBody?["messages"]?.AsArray();
        var upstreamAssistant = upstreamMessages?.ElementAtOrDefault(1);
        var upstreamReasoning = upstreamAssistant?["reasoning_content"]?.ToString();

        Assert.IsNotNull(upstreamReasoning,
            "Second request's upstream assistant message should have reasoning_content");
        StringAssert.Contains(upstreamReasoning, "multiple angles",
            "Upstream reasoning_content should contain the original thinking text");
    }
}
