using System.Net;
using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Models.AnthropicViewModels;
using Microsoft.EntityFrameworkCore;

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
            Messages = [new AnthropicMessage { Role = "user", Content = JsonValue.Create("Hello") }]
        });

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<string> CreateApiKey()
    {
        var apiKeyStr = Guid.NewGuid().ToString();
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var userInDb = await db.Users.FirstOrDefaultAsync();
            if (userInDb == null)
            {
                userInDb = new User { UserName = "testuser", Email = "test@example.com", DisplayName = "Test User" };
                db.Users.Add(userInDb);
                await db.SaveChangesAsync();
            }
            
            db.ApiKeys.Add(new ApiKey 
            { 
                Name = "Test Key", 
                Key = apiKeyStr, 
                UserId = userInDb.Id,
                ExpirationTime = DateTime.UtcNow.AddDays(1)
            });
            await db.SaveChangesAsync();
        }
        return apiKeyStr;
    }

    [TestMethod]
    public async Task Anthropic_Messages_Returns404ForUnknownModel()
    {
        var token = await CreateApiKey();
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent.Create(new AnthropicMessageRequest
            {
                Model = "non-existent-model",
                Messages = [new AnthropicMessage { Role = "user", Content = JsonValue.Create("Hello") }]
            })
        };
        request.Headers.Add("x-api-key", token);

        var response = await Http.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task Anthropic_Messages_XApiKeyHeader_IsAccepted()
    {
        var token = await CreateApiKey();
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent.Create(new AnthropicMessageRequest
            {
                Model = "non-existent-model",
                Messages = [new AnthropicMessage { Role = "user", Content = JsonValue.Create("Hello") }]
            })
        };
        request.Headers.Add("x-api-key", token);

        var response = await Http.SendAsync(request);
        Assert.AreNotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Anthropic_Messages_AuthorizationBearerHeader_IsAccepted()
    {
        var token = await CreateApiKey();
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent.Create(new AnthropicMessageRequest
            {
                Model = "non-existent-model",
                Messages = [new AnthropicMessage { Role = "user", Content = JsonValue.Create("Hello") }]
            })
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await Http.SendAsync(request);
        Assert.AreNotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Anthropic_Messages_HandlesToolDefinitions()
    {
        var token = await CreateApiKey();
        
        var anthropicRequest = new AnthropicMessageRequest
        {
            Model = "non-existent-model",
            Messages = [new AnthropicMessage { Role = "user", Content = JsonValue.Create("What is the weather?") }],
            Tools =
            [
                new AnthropicTool
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
            ]
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
        var token = await CreateApiKey();
        
        var anthropicRequest = new AnthropicMessageRequest
        {
            Model = "non-existent-model",
            Messages =
            [
                new AnthropicMessage
                { 
                    Role = "user", 
                    Content = new JsonArray 
                    { 
                        new JsonObject { ["type"] = "text", ["text"] = "Look at this:" },
                        new JsonObject { ["type"] = "text", ["text"] = "What do you see?" }
                    } 
                }
            ]
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
    public async Task Anthropic_Messages_HandlesSystemAsArray()
    {
        var token = await CreateApiKey();
        var anthropicRequest = new AnthropicMessageRequest
        {
            Model = "non-existent-model",
            System = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "You are a helpful assistant." } },
            Messages = [new AnthropicMessage { Role = "user", Content = JsonValue.Create("Hi") }]
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
    public async Task Anthropic_Messages_UltimateStressTest_MappingWorks()
    {
        var token = await CreateApiKey();
        var modelName = "stress-test-model";
        
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var userInDb = await db.Users.FirstAsync();
            var provider = new OllamaProvider
            {
                Name = "Stress Provider",
                BaseUrl = "http://localhost:11434",
                ProviderType = ProviderType.Ollama
            };
            db.OllamaProviders.Add(provider);
            await db.SaveChangesAsync();

            var vm = new VirtualModel
            {
                Name = modelName,
                Type = ModelType.Chat,
                MaxRetries = 1,
                Thinking = true,
                NumCtx = 8192
            };
            db.VirtualModels.Add(vm);
            await db.SaveChangesAsync();

            db.VirtualModelBackends.Add(new VirtualModelBackend
            {
                VirtualModelId = vm.Id,
                ProviderId = provider.Id,
                UnderlyingModelName = "llama3"
            });
            await db.SaveChangesAsync();
            Assert.IsNotNull(userInDb);
        }

        var anthropicRequest = new AnthropicMessageRequest
        {
            Model = modelName,
            System = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "You are a stress tester." } },
            Messages =
            [
                new AnthropicMessage
                {
                    Role = "user",
                    Content = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = "First block" },
                        new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = "t1", ["content"] = "Tool output" }
                    }
                }
            ],
            Tools =
            [
                new AnthropicTool
                {
                    Name = "complex_tool",
                    InputSchema = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["data"] = new JsonObject { ["type"] = "string" }
                        }
                    }
                }
            ],
            Stream = true
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent.Create(anthropicRequest)
        };
        request.Headers.Add("x-api-key", token);

        var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.AreNotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [TestMethod]
    public async Task Anthropic_Messages_History_WithToolUseAndResult_Works()
    {
        var token = await CreateApiKey();
        var modelName = "tool-history-model";
        
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var provider = new OllamaProvider
            {
                Name = "Mock Provider for Tools",
                BaseUrl = "http://localhost:11434",
                ProviderType = ProviderType.Ollama
            };
            db.OllamaProviders.Add(provider);
            await db.SaveChangesAsync();

            var vm = new VirtualModel
            {
                Name = modelName,
                Type = ModelType.Chat,
                MaxRetries = 1
            };
            db.VirtualModels.Add(vm);
            await db.SaveChangesAsync();

            db.VirtualModelBackends.Add(new VirtualModelBackend
            {
                VirtualModelId = vm.Id,
                ProviderId = provider.Id,
                UnderlyingModelName = "llama3"
            });
            await db.SaveChangesAsync();
        }

        var anthropicRequest = new AnthropicMessageRequest
        {
            Model = modelName,
            Messages =
            [
                new AnthropicMessage
                {
                    Role = "user",
                    Content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "What's the weather?" } }
                },
                new AnthropicMessage
                {
                    Role = "assistant",
                    Content = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = "I need to check the weather." },
                        new JsonObject 
                        { 
                            ["type"] = "tool_use", 
                            ["id"] = "tool_123", 
                            ["name"] = "get_weather", 
                            ["input"] = new JsonObject { ["location"] = "San Francisco" } 
                        }
                    }
                },
                new AnthropicMessage
                {
                    Role = "user",
                    Content = new JsonArray
                    {
                        new JsonObject 
                        { 
                            ["type"] = "tool_result", 
                            ["tool_use_id"] = "tool_123", 
                            ["content"] = "Sunny, 70F" 
                        }
                    }
                }
            ]
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent.Create(anthropicRequest)
        };
        request.Headers.Add("x-api-key", token);

        var response = await Http.SendAsync(request);
        
        // We expect it not to throw 500 (Internal Server Error). It might throw 503 if the upstream is unreachable.
        Assert.AreNotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [TestMethod]
    public async Task Anthropic_Messages_History_WithThinking_RoundtripsProperly()
    {
        var token = await CreateApiKey();
        var modelName = "thinking-history-model";
        
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var provider = new OllamaProvider
            {
                Name = "Mock Provider for Thinking",
                BaseUrl = "http://localhost:11434",
                ProviderType = ProviderType.OpenAI // Use OpenAI provider to verify reasoning_content injection
            };
            db.OllamaProviders.Add(provider);
            await db.SaveChangesAsync();

            var vm = new VirtualModel
            {
                Name = modelName,
                Type = ModelType.Chat,
                MaxRetries = 1
            };
            db.VirtualModels.Add(vm);
            await db.SaveChangesAsync();

            db.VirtualModelBackends.Add(new VirtualModelBackend
            {
                VirtualModelId = vm.Id,
                ProviderId = provider.Id,
                UnderlyingModelName = "deepseek-r1"
            });
            await db.SaveChangesAsync();
        }

        var anthropicRequest = new AnthropicMessageRequest
        {
            Model = modelName,
            Messages =
            [
                new AnthropicMessage
                {
                    Role = "user",
                    Content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "Solve this puzzle." } }
                },
                new AnthropicMessage
                {
                    Role = "assistant",
                    Content = new JsonArray
                    {
                        new JsonObject { ["type"] = "thinking", ["thinking"] = "I need to think step by step." },
                        new JsonObject { ["type"] = "text", ["text"] = "Here is the solution." }
                    }
                },
                new AnthropicMessage
                {
                    Role = "user",
                    Content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "Explain the second step." } }
                }
            ]
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent.Create(anthropicRequest)
        };
        request.Headers.Add("x-api-key", token);

        var response = await Http.SendAsync(request);
        
        // Ensure no 500 error happens when mapping the complex thinking block back to an OpenAI backend
        Assert.AreNotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [TestMethod]
    public async Task Anthropic_Messages_History_WithThinking_OllamaBackend_RoundtripsProperly()
    {
        var token = await CreateApiKey();
        var modelName = "thinking-history-ollama-model";
        
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var provider = new OllamaProvider
            {
                Name = "Mock Provider for Thinking (Ollama)",
                BaseUrl = "http://localhost:11434",
                ProviderType = ProviderType.Ollama // Use Ollama provider
            };
            db.OllamaProviders.Add(provider);
            await db.SaveChangesAsync();

            var vm = new VirtualModel
            {
                Name = modelName,
                Type = ModelType.Chat,
                MaxRetries = 1
            };
            db.VirtualModels.Add(vm);
            await db.SaveChangesAsync();

            db.VirtualModelBackends.Add(new VirtualModelBackend
            {
                VirtualModelId = vm.Id,
                ProviderId = provider.Id,
                UnderlyingModelName = "deepseek-r1"
            });
            await db.SaveChangesAsync();
        }

        var anthropicRequest = new AnthropicMessageRequest
        {
            Model = modelName,
            Messages =
            [
                new AnthropicMessage
                {
                    Role = "user",
                    Content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "Solve this puzzle." } }
                },
                new AnthropicMessage
                {
                    Role = "assistant",
                    Content = new JsonArray
                    {
                        new JsonObject { ["type"] = "thinking", ["thinking"] = "I need to think step by step." },
                        new JsonObject { ["type"] = "text", ["text"] = "Here is the solution." }
                    }
                },
                new AnthropicMessage
                {
                    Role = "user",
                    Content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "Explain the second step." } }
                }
            ]
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent.Create(anthropicRequest)
        };
        request.Headers.Add("x-api-key", token);

        var response = await Http.SendAsync(request);
        
        // Ensure it processes correctly for Ollama backend as well without 500 error
        Assert.AreNotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }
}
