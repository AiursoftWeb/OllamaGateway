using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Gateway;
using Aiursoft.OllamaGateway.Gateway.Chat;

namespace Aiursoft.OllamaGateway.Tests.Gateway.Chat;

[TestClass]
public class ChatRequestCompilerTests
{
    private readonly ChatRequestCompiler _compiler = new(
        [new OpenAiChatRequestDecoder(), new OllamaChatRequestDecoder(), new AnthropicChatRequestDecoder()],
        [new OpenAiChatProviderRequestAdapter(), new OllamaChatProviderRequestAdapter()]);

    [TestMethod]
    public async Task OpenAiToOllama_CompilesImagesToolsAndOverrides()
    {
        const string body = """
        {
          "model":"virtual",
          "stream":true,
          "messages":[
            {"role":"user","content":[{"type":"text","text":"see "},{"type":"image_url","image_url":{"url":"data:image/jpeg;base64,AQID"}}]},
            {"role":"assistant","content":"","tool_calls":[{"id":"call_1","type":"function","function":{"name":"lookup","arguments":"{\"q\":\"x\"}"}}]}
          ],
          "tools":[{"type":"function","function":{"name":"lookup","description":"Lookup","parameters":{"type":"object"}}}],
          "temperature":0.2
        }
        """;
        var decoded = _compiler.Decode(ProtocolDialect.OpenAiChatCompletions, body);
        var backend = Backend(ProviderType.Ollama, "llama-physical");
        var virtualModel = Model(temperature: 0.7f);

        using var request = _compiler.CreateProviderRequest(decoded, virtualModel, backend);
        var json = JsonNode.Parse(await request.Content!.ReadAsStringAsync());

        Assert.AreEqual("/api/chat", request.RequestUri?.AbsolutePath);
        Assert.AreEqual("llama-physical", json?["model"]?.ToString());
        Assert.AreEqual("see ", json?["messages"]?[0]?["content"]?.ToString());
        Assert.AreEqual("AQID", json?["messages"]?[0]?["images"]?[0]?.ToString());
        Assert.AreEqual("x", json?["messages"]?[1]?["tool_calls"]?[0]?["function"]?["arguments"]?["q"]?.ToString());
        Assert.AreEqual("lookup", json?["tools"]?[0]?["function"]?["name"]?.ToString());
        Assert.AreEqual(0.7, json?["options"]?["temperature"]?.GetValue<double>());
    }

    [TestMethod]
    public async Task OpenAiToOpenAi_PreservesUnknownFields()
    {
        const string body = """{"model":"virtual","messages":[{"role":"user","content":null}],"stream":false,"response_format":{"type":"json_schema"},"vendor_extension":{"exact":1.2300}}""";
        var decoded = _compiler.Decode(ProtocolDialect.OpenAiChatCompletions, body);

        using var request = _compiler.CreateProviderRequest(decoded, Model(), Backend(ProviderType.OpenAI, "gpt-physical"));
        var json = JsonNode.Parse(await request.Content!.ReadAsStringAsync());

        Assert.AreEqual("gpt-physical", json?["model"]?.ToString());
        Assert.AreEqual("json_schema", json?["response_format"]?["type"]?.ToString());
        Assert.AreEqual("1.2300", json?["vendor_extension"]?["exact"]?.ToJsonString());
        Assert.AreEqual(string.Empty, json?["messages"]?[0]?["content"]?.ToString());
    }

    [TestMethod]
    public async Task OllamaToOllama_PreservesToolsAndDatabaseOverrideWins()
    {
        const string body = """{"model":"virtual","messages":[{"role":"user","content":"hi"}],"tools":[{"type":"function","function":{"name":"read","parameters":{}}}],"tool_choice":"auto","options":{"repeat_penalty":1.1}}""";
        var decoded = _compiler.Decode(ProtocolDialect.OllamaNative, body);

        using var request = _compiler.CreateProviderRequest(decoded, Model(repeatPenalty: 1.8f), Backend(ProviderType.Ollama, "llama"));
        var json = JsonNode.Parse(await request.Content!.ReadAsStringAsync());

        Assert.AreEqual("read", json?["tools"]?[0]?["function"]?["name"]?.ToString());
        Assert.AreEqual("auto", json?["tool_choice"]?.ToString());
        Assert.AreEqual(1.8, json?["options"]?["repeat_penalty"]?.GetValue<double>());
    }

    [TestMethod]
    public async Task AnthropicToOpenAi_CompilesThinkingToolUseAndToolResult()
    {
        const string body = """
        {
          "model":"virtual","max_tokens":123,"messages":[
            {"role":"assistant","content":[{"type":"thinking","thinking":"reason"},{"type":"tool_use","id":"tool_1","name":"read","input":{"path":"a"}}]},
            {"role":"user","content":[{"type":"tool_result","tool_use_id":"tool_1","content":"ok"}]}
          ]
        }
        """;
        var decoded = _compiler.Decode(ProtocolDialect.AnthropicMessages, body);

        using var request = _compiler.CreateProviderRequest(decoded, Model(), Backend(ProviderType.OpenAI, "gpt"));
        var json = JsonNode.Parse(await request.Content!.ReadAsStringAsync());

        Assert.AreEqual("reason", json?["messages"]?[0]?["reasoning_content"]?.ToString());
        Assert.AreEqual("{\"path\":\"a\"}", json?["messages"]?[0]?["tool_calls"]?[0]?["function"]?["arguments"]?.ToString());
        Assert.AreEqual("tool", json?["messages"]?[1]?["role"]?.ToString());
        Assert.AreEqual("tool_1", json?["messages"]?[1]?["tool_call_id"]?.ToString());
        Assert.AreEqual(123, json?["max_tokens"]?.GetValue<int>());
    }

    [TestMethod]
    public async Task AnthropicHistory_PreservesThinkTagsAndToolResultOrder()
    {
        const string body = """
        {
          "model":"virtual","messages":[
            {"role":"assistant","content":"<think>private reason</think>visible answer"},
            {"role":"user","content":[
              {"type":"text","text":"before"},
              {"type":"tool_result","tool_use_id":"tool_1","content":"result"},
              {"type":"text","text":"after"}
            ]}
          ]
        }
        """;
        var decoded = _compiler.Decode(ProtocolDialect.AnthropicMessages, body);

        using var request = _compiler.CreateProviderRequest(decoded, Model(), Backend(ProviderType.OpenAI, "gpt"));
        var messages = JsonNode.Parse(await request.Content!.ReadAsStringAsync())?["messages"]?.AsArray();

        Assert.IsNotNull(messages);
        Assert.AreEqual("private reason", messages[0]?["reasoning_content"]?.ToString());
        Assert.AreEqual("visible answer", messages[0]?["content"]?.ToString());
        Assert.AreEqual("before", messages[1]?["content"]?.ToString());
        Assert.AreEqual("tool", messages[2]?["role"]?.ToString());
        Assert.AreEqual("result", messages[2]?["content"]?.ToString());
        Assert.AreEqual("after", messages[3]?["content"]?.ToString());
    }

    private static VirtualModel Model(float? temperature = null, float? repeatPenalty = null)
    {
        return new VirtualModel
        {
            Name = "virtual",
            Type = ModelType.Chat,
            Temperature = temperature,
            RepeatPenalty = repeatPenalty
        };
    }

    private static VirtualModelBackend Backend(ProviderType type, string physicalModel)
    {
        return new VirtualModelBackend
        {
            UnderlyingModelName = physicalModel,
            Provider = new OllamaProvider
            {
                Name = "provider",
                BaseUrl = "https://provider.test",
                ProviderType = type
            }
        };
    }
}
