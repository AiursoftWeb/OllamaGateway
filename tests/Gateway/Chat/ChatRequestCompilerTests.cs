using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Gateway;
using Aiursoft.OllamaGateway.Gateway.Chat;
using Aiursoft.OllamaGateway.Gateway.Execution;

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
          "temperature":0.2,
          "chat_template_kwargs":{"enable_thinking":true}
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
        Assert.AreEqual(true, json?["think"]?.GetValue<bool>());
    }

    [TestMethod]
    public async Task OpenAiToOpenAi_PreservesUnknownFields()
    {
        const string body = """{"model":"virtual","messages":[{"role":"user","content":null}],"stream":true,"stream_options":{"future_option":"kept"},"response_format":{"type":"json_schema","future_option":"kept"},"chat_template_kwargs":{"enable_thinking":true,"future_option":"kept"},"max_completion_tokens":100,"vendor_extension":{"exact":1.2300},"top_k":40,"num_ctx":8192,"repeat_penalty":1.2}""";
        var decoded = _compiler.Decode(ProtocolDialect.OpenAiChatCompletions, body);
        Assert.IsTrue(decoded.Request.RequiredCapabilities.HasFlag(GatewayCapability.OpenAiChatPassthrough));

        using var request = _compiler.CreateProviderRequest(decoded, Model(), Backend(ProviderType.OpenAI, "gpt-physical"));
        var json = JsonNode.Parse(await request.Content!.ReadAsStringAsync());

        Assert.AreEqual("gpt-physical", json?["model"]?.ToString());
        Assert.AreEqual("json_schema", json?["response_format"]?["type"]?.ToString());
        Assert.AreEqual("kept", json?["response_format"]?["future_option"]?.ToString());
        Assert.AreEqual("kept", json?["chat_template_kwargs"]?["future_option"]?.ToString());
        Assert.AreEqual("kept", json?["stream_options"]?["future_option"]?.ToString());
        Assert.AreEqual(true, json?["stream_options"]?["include_usage"]?.GetValue<bool>());
        Assert.AreEqual(100, json?["max_completion_tokens"]?.GetValue<int>());
        Assert.IsNull(json?["max_tokens"]);
        Assert.AreEqual("1.2300", json?["vendor_extension"]?["exact"]?.ToJsonString());
        Assert.AreEqual(40, json?["top_k"]?.GetValue<int>());
        Assert.AreEqual(8192, json?["num_ctx"]?.GetValue<int>());
        Assert.AreEqual(1.2, json?["repeat_penalty"]?.GetValue<double>());
        Assert.AreEqual(string.Empty, json?["messages"]?[0]?["content"]?.ToString());
    }

    [TestMethod]
    public async Task OllamaToOpenAi_ConvertsSharedOptionsAndDropsOllamaOnlyOptions()
    {
        const string body = """{"model":"virtual","messages":[{"role":"user","content":"hi"}],"think":true,"options":{"temperature":0.3,"top_p":0.8,"top_k":40,"num_ctx":8192,"repeat_penalty":1.2}}""";
        var decoded = _compiler.Decode(ProtocolDialect.OllamaNative, body);

        using var request = _compiler.CreateProviderRequest(
            decoded,
            Model(),
            Backend(ProviderType.OpenAI, "gpt-physical"));
        var json = JsonNode.Parse(await request.Content!.ReadAsStringAsync());

        Assert.AreEqual(0.3, json?["temperature"]?.GetValue<double>());
        Assert.AreEqual(0.8, json?["top_p"]?.GetValue<double>());
        Assert.AreEqual(true, json?["chat_template_kwargs"]?["enable_thinking"]?.GetValue<bool>());
        Assert.IsNull(json?["top_k"]);
        Assert.IsNull(json?["num_ctx"]);
        Assert.IsNull(json?["repeat_penalty"]);
        Assert.IsNull(json?["options"]);
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
    public async Task AnthropicBase64Image_CompilesToOpenAiDataUrl()
    {
        const string body = """
        {
          "model":"virtual","messages":[{"role":"user","content":[
            {"type":"text","text":"describe"},
            {"type":"image","source":{"type":"base64","media_type":"image/jpeg","data":"AQID"}}
          ]}]
        }
        """;
        var decoded = _compiler.Decode(ProtocolDialect.AnthropicMessages, body);

        using var request = _compiler.CreateProviderRequest(
            decoded,
            Model(),
            Backend(ProviderType.OpenAI, "gpt-vision"));
        var content = JsonNode.Parse(await request.Content!.ReadAsStringAsync())?["messages"]?[0]?["content"];

        Assert.AreEqual("describe", content?[0]?["text"]?.ToString());
        Assert.AreEqual(
            "data:image/jpeg;base64,AQID",
            content?[1]?["image_url"]?["url"]?.ToString());
    }

    [TestMethod]
    public async Task AnthropicUrlImage_CompilesToOpenAiImageUrl()
    {
        const string body = """
        {
          "model":"virtual","messages":[{"role":"user","content":[
            {"type":"text","text":"describe"},
            {"type":"image","source":{"type":"url","url":"https://images.example.test/cat.png"}}
          ]}]
        }
        """;
        var decoded = _compiler.Decode(ProtocolDialect.AnthropicMessages, body);

        using var request = _compiler.CreateProviderRequest(
            decoded,
            Model(),
            Backend(ProviderType.OpenAI, "gpt-vision"));
        var imageUrl = JsonNode.Parse(await request.Content!.ReadAsStringAsync())
            ?["messages"]?[0]?["content"]?[1]?["image_url"]?["url"]?.ToString();

        Assert.AreEqual("https://images.example.test/cat.png", imageUrl);
    }

    [TestMethod]
    public async Task AnthropicBase64Image_CompilesToOllamaImagesArray()
    {
        const string body = """
        {
          "model":"virtual","messages":[{"role":"user","content":[
            {"type":"text","text":"describe"},
            {"type":"image","source":{"type":"base64","media_type":"image/png","data":"BAUG"}}
          ]}]
        }
        """;
        var decoded = _compiler.Decode(ProtocolDialect.AnthropicMessages, body);

        using var request = _compiler.CreateProviderRequest(
            decoded,
            Model(),
            Backend(ProviderType.Ollama, "llava"));
        var message = JsonNode.Parse(await request.Content!.ReadAsStringAsync())?["messages"]?[0];

        Assert.AreEqual("describe", message?["content"]?.ToString());
        Assert.AreEqual("BAUG", message?["images"]?[0]?.ToString());
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
