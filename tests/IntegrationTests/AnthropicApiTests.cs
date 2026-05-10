using System.Net;
using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Models.AnthropicViewModels;

namespace Aiursoft.OllamaGateway.Tests.IntegrationTests;

[TestClass]
public class AnthropicApiTests : TestBase
{
    [TestMethod]
    public async Task Anthropic_Messages_Returns401WithoutAuth()
    {
        var response = await Http.PostAsJsonAsync("/v1/messages", new AnthropicMessageRequest
        {
            Model = "test-model",
            Messages = new List<AnthropicMessage> { new() { Role = "user", Content = JsonValue.Create("Hello") } }
        });

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private string CreateApiKeySync()
    {
        string apiKeyStr = Guid.NewGuid().ToString();
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var user = db.Users.FirstOrDefault();
            if (user == null)
            {
                user = new User { UserName = "testuser", Email = "test@example.com", DisplayName = "Test User" };
                db.Users.Add(user);
                db.SaveChanges();
            }

            db.ApiKeys.Add(new ApiKey
            {
                Name = "Test Key",
                Key = apiKeyStr,
                UserId = user.Id,
                ExpirationTime = DateTime.UtcNow.AddDays(1)
            });
            db.SaveChanges();
        }
        return apiKeyStr;
    }

    [TestMethod]
    public async Task Anthropic_Messages_HandlesSystemAsArray()
    {
        var token = CreateApiKeySync();
        var anthropicRequest = new AnthropicMessageRequest
        {
            Model = "non-existent-model",
            System = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "You are a helpful assistant." } },
            Messages = new List<AnthropicMessage> { new() { Role = "user", Content = JsonValue.Create("Hi") } }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent.Create(anthropicRequest)
        };
        request.Headers.Add("x-api-key", token);

        var response = await Http.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task Anthropic_Messages_HandlesToolResultInContent()
    {
        var token = CreateApiKeySync();
        var anthropicRequest = new AnthropicMessageRequest
        {
            Model = "non-existent-model",
            Messages = new List<AnthropicMessage>
            {
                new()
                {
                    Role = "user",
                    Content = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = "toolu_123",
                            ["content"] = "The result of the tool execution"
                        }
                    }
                }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent.Create(anthropicRequest)
        };
        request.Headers.Add("x-api-key", token);

        var response = await Http.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task Anthropic_Messages_Returns404ForUnknownModel()
    {
        var token = CreateApiKeySync();
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent.Create(new AnthropicMessageRequest
            {
                Model = "non-existent-model",
                Messages = new List<AnthropicMessage> { new() { Role = "user", Content = JsonValue.Create("Hello") } }
            })
        };
        request.Headers.Add("x-api-key", token);

        var response = await Http.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task Anthropic_Messages_XApiKeyHeader_IsAccepted()
    {
        var token = CreateApiKeySync();
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent.Create(new AnthropicMessageRequest
            {
                Model = "non-existent-model",
                Messages = new List<AnthropicMessage> { new() { Role = "user", Content = JsonValue.Create("Hello") } }
            })
        };
        request.Headers.Add("x-api-key", token);

        var response = await Http.SendAsync(request);
        Assert.AreNotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Anthropic_Messages_AuthorizationBearerHeader_IsAccepted()
    {
        var token = CreateApiKeySync();
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent.Create(new AnthropicMessageRequest
            {
                Model = "non-existent-model",
                Messages = new List<AnthropicMessage> { new() { Role = "user", Content = JsonValue.Create("Hello") } }
            })
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await Http.SendAsync(request);
        Assert.AreNotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Anthropic_Messages_HandlesToolDefinitions()
    {
        var token = CreateApiKeySync();

        var anthropicRequest = new AnthropicMessageRequest
        {
            Model = "non-existent-model",
            Messages = new List<AnthropicMessage> { new() { Role = "user", Content = JsonValue.Create("What is the weather?") } },
            Tools = new List<AnthropicTool>
            {
                new()
                {
                    Name = "get_weather",
                    Description = "Get the current weather",
                    InputSchema = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["location"] = new JsonObject { ["type"] = "string" }
                        }
                    }
                }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent.Create(anthropicRequest)
        };
        request.Headers.Add("x-api-key", token);

        var response = await Http.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task Anthropic_Messages_HandlesComplexContentArrays()
    {
        var token = CreateApiKeySync();

        var anthropicRequest = new AnthropicMessageRequest
        {
            Model = "non-existent-model",
            Messages = new List<AnthropicMessage>
            {
                new()
                {
                    Role = "user",
                    Content = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = "Look at this:" },
                        new JsonObject { ["type"] = "text", ["text"] = "What do you see?" }
                    }
                }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent.Create(anthropicRequest)
        };
        request.Headers.Add("x-api-key", token);

        var response = await Http.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }
}
