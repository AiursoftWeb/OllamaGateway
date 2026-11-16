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
        [new OpenAiChatRequestDecoder(), new OllamaChatRequestDecoder(), new AnthropicChatRequestDecoder(), new OpenAiResponsesRequestDecoder()],
        [new OpenAiChatProviderRequestAdapter(), new OllamaChatProviderRequestAdapter(), new OpenAiResponsesProviderRequestAdapter()]);

    [TestMethod]
    [DataRow(ProtocolDialect.OpenAiChatCompletions, BackendProtocol.OpenAiChatCompletions)]
    [DataRow(ProtocolDialect.OpenAiResponses, BackendProtocol.OpenAiResponses)]
    [DataRow(ProtocolDialect.OpenAiChatCompletions, BackendProtocol.OpenAiResponses)]
    [DataRow(ProtocolDialect.OpenAiResponses, BackendProtocol.OpenAiChatCompletions)]
    [DataRow(ProtocolDialect.OllamaNative, BackendProtocol.OpenAiChatCompletions)]
    [DataRow(ProtocolDialect.OllamaNative, BackendProtocol.OpenAiResponses)]
    [DataRow(ProtocolDialect.AnthropicMessages, BackendProtocol.OpenAiChatCompletions)]
    [DataRow(ProtocolDialect.AnthropicMessages, BackendProtocol.OpenAiResponses)]
    public async Task OpenAiBackend_OverridesEffortInDirectAndTranslatedRequests(ProtocolDialect source, BackendProtocol target)
    {
        var raw = source switch
        {
            ProtocolDialect.OpenAiResponses => """{"model":"virtual","input":"hi","reasoning":{"effort":"low","summary":"auto","vendor_flag":true},"vendor_extension":42}""",
            ProtocolDialect.OllamaNative => """{"model":"virtual","messages":[{"role":"user","content":"hi"}],"think":"low"}""",
            _ => """{"model":"virtual","messages":[{"role":"user","content":"hi"}],"reasoning_effort":"low","vendor_extension":42,"chat_template_kwargs":{"enable_thinking":false,"vendor_flag":true}}"""
        };
        var decoded = _compiler.Decode(source, raw);
        var original = decoded.OriginalBody.ToJsonString();
        var backend = Backend(ProviderType.OpenAI, "physical");
        backend.Protocol = target;
        foreach (var effort in new[] { "medium", "future-model-level", null })
        {
            backend.ReasoningEffortOverride = effort;
            using var request = _compiler.CreateProviderRequest(decoded, Model(), backend);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync());
            var actual = target == BackendProtocol.OpenAiResponses ? body?["reasoning"]?["effort"] : body?["reasoning_effort"];
            Assert.AreEqual(effort ?? (source == ProtocolDialect.AnthropicMessages ? null : "low"), actual?.ToString());
            if ((source == ProtocolDialect.OpenAiResponses && target == BackendProtocol.OpenAiResponses)
                || (source == ProtocolDialect.OpenAiChatCompletions && target == BackendProtocol.OpenAiChatCompletions))
            {
                Assert.AreEqual(42, body?["vendor_extension"]?.GetValue<int>());
                if (source == ProtocolDialect.OpenAiResponses)
                {
                    Assert.AreEqual("auto", body?["reasoning"]?["summary"]?.ToString());
                    Assert.AreEqual(true, body?["reasoning"]?["vendor_flag"]?.GetValue<bool>());
                }
                else Assert.AreEqual(false, body?["chat_template_kwargs"]?["enable_thinking"]?.GetValue<bool>());
            }
            Assert.AreEqual(original, decoded.OriginalBody.ToJsonString(), "Fallback attempts must start from the original client request.");
        }
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("\"none\"")]
    [DataRow("\"false\"")]
    public async Task NativeOllama_NoOverride_PreservesRawThink(string value)
    {
        var decoded = _compiler.Decode(ProtocolDialect.OllamaNative,
            "{\"model\":\"virtual\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"think\":" + value + "}");
        using var request = _compiler.CreateProviderRequest(decoded, Model(), Backend(ProviderType.Ollama, "physical"));
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
        Assert.IsTrue(body.AsObject().ContainsKey("think"));
        Assert.IsTrue(JsonNode.DeepEquals(decoded.OriginalBody["think"], body["think"]));
    }

    [TestMethod]
    [DataRow(null, "medium", "medium")]
    [DataRow(null, "future-level", "future-level")]
    [DataRow(true, "medium", "true")]
    [DataRow(false, "medium", "false")]
    public async Task OllamaBackend_OverridesOnlyItsOwnThinking(bool? forced, string clientEffort, string expected)
    {
        var decoded = _compiler.Decode(ProtocolDialect.OllamaNative,
            $$"""{"model":"virtual","messages":[{"role":"user","content":"hi"}],"think":"{{clientEffort}}"}""");
        var backend = Backend(ProviderType.Ollama, "physical");
        backend.OllamaThinkOverride = forced;
        using var request = _compiler.CreateProviderRequest(decoded, Model(), backend);
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync());
        Assert.AreEqual(expected, body?["think"]?.ToString().ToLowerInvariant());
        Assert.AreEqual(clientEffort, decoded.OriginalBody["think"]?.ToString(), "An attempt must not mutate the client request.");

        backend.OllamaThinkOverride = null;
        using var fallback = _compiler.CreateProviderRequest(decoded, Model(), backend);
        var fallbackBody = JsonNode.Parse(await fallback.Content!.ReadAsStringAsync());
        Assert.AreEqual(clientEffort, fallbackBody?["think"]?.GetValue<string>());
    }

    [TestMethod]
    [DataRow(ProtocolDialect.OpenAiChatCompletions, BackendProtocol.OpenAiChatCompletions)]
    [DataRow(ProtocolDialect.OpenAiResponses, BackendProtocol.OpenAiResponses)]
    [DataRow(ProtocolDialect.OpenAiChatCompletions, BackendProtocol.OpenAiResponses)]
    [DataRow(ProtocolDialect.OpenAiResponses, BackendProtocol.OpenAiChatCompletions)]
    public async Task OpenAiProtocols_PreserveClientEffortAndIgnoreOllamaOverride(ProtocolDialect client, BackendProtocol upstream)
    {
        var raw = client == ProtocolDialect.OpenAiChatCompletions
            ? """{"model":"virtual","messages":[{"role":"user","content":"hi"}],"reasoning_effort":"xhigh"}"""
            : """{"model":"virtual","input":"hi","reasoning":{"effort":"xhigh","summary":"auto"}}""";
        var backend = Backend(ProviderType.OpenAI, "physical");
        backend.Protocol = upstream;
        backend.OllamaThinkOverride = false;
        using var request = _compiler.CreateProviderRequest(_compiler.Decode(client, raw), Model(), backend);
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync());
        Assert.AreEqual("xhigh", (upstream == BackendProtocol.OpenAiChatCompletions
            ? body?["reasoning_effort"] : body?["reasoning"]?["effort"])?.ToString());
        Assert.IsNull(body?["chat_template_kwargs"]);
        Assert.IsNull(body?["think"]);
        if (client == ProtocolDialect.OpenAiResponses && upstream == BackendProtocol.OpenAiResponses)
            Assert.AreEqual("auto", body?["reasoning"]?["summary"]?.ToString());
    }

    [TestMethod]
    [DataRow(ProtocolDialect.OpenAiChatCompletions)]
    [DataRow(ProtocolDialect.OpenAiResponses)]
    public async Task OpenAiToOllama_WithoutOverride_PreservesNamedEffort(ProtocolDialect dialect)
    {
        var raw = dialect == ProtocolDialect.OpenAiChatCompletions
            ? """{"model":"virtual","messages":[{"role":"user","content":"hi"}],"reasoning_effort":"medium"}"""
            : """{"model":"virtual","input":"hi","reasoning":{"effort":"medium"}}""";
        using var request = _compiler.CreateProviderRequest(_compiler.Decode(dialect, raw), Model(), Backend(ProviderType.Ollama, "physical"));
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync());
        Assert.AreEqual("medium", body?["think"]?.GetValue<string>());
    }

    [TestMethod]
    [DataRow("[]")]
    [DataRow("null")]
    [DataRow("invalid-json")]
    [DataRow("[{\"Name\":\"another-model\"}]")]
    public async Task UnselectedOrInvalidWarmup_PreservesProviderKeepAlive(string warmupModels)
    {
        var backend = Backend(ProviderType.Ollama, "llama-physical");
        backend.Provider!.KeepAlive = "10m";
        backend.Provider.WarmupModelsJson = warmupModels;
        var decoded = _compiler.Decode(ProtocolDialect.OllamaNative,
            """{"model":"virtual","messages":[{"role":"user","content":"hi"}]}""");
        using var request = _compiler.CreateProviderRequest(decoded, Model(), backend);
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync());
        Assert.AreEqual("10m", body?["keep_alive"]?.ToString());
    }

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
        Assert.IsFalse(decoded.Request.RequiredCapabilities.HasFlag(GatewayCapability.OpenAiChatPassthrough));
        Assert.IsTrue(decoded.Request.PreferredCapabilities.HasFlag(GatewayCapability.OpenAiChatPassthrough));

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
    public async Task OllamaToOpenAi_PreservesToolCallAndResultIds()
    {
        const string body = """
        {
          "model":"virtual",
          "messages":[
            {"role":"user","content":"What time is it?"},
            {"role":"assistant","content":"","tool_calls":[{
              "id":"call_timestamp_1",
              "function":{"name":"get_current_timestamp","arguments":{}}
            }]},
            {"role":"tool","tool_call_id":"call_timestamp_1","content":"{\"current_iso\":\"2026-08-17T12:34:45Z\"}"}
          ]
        }
        """;
        var decoded = _compiler.Decode(ProtocolDialect.OllamaNative, body);

        using var request = _compiler.CreateProviderRequest(
            decoded,
            Model(),
            Backend(ProviderType.OpenAI, "gpt-physical"));
        var messages = JsonNode.Parse(await request.Content!.ReadAsStringAsync())?["messages"]?.AsArray();

        Assert.IsNotNull(messages);
        Assert.AreEqual(3, messages.Count);
        Assert.AreEqual("call_timestamp_1", messages[1]?["tool_calls"]?[0]?["id"]?.ToString());
        Assert.AreEqual("tool", messages[2]?["role"]?.ToString());
        Assert.AreEqual("call_timestamp_1", messages[2]?["tool_call_id"]?.ToString());
        Assert.AreEqual(
            "{\"current_iso\":\"2026-08-17T12:34:45Z\"}",
            messages[2]?["content"]?.ToString());
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
    public void AnthropicClaudeCodeControls_AreSoftPassthroughPreference()
    {
        const string body = """
        {
          "model":"virtual",
          "max_tokens":1024,
          "messages":[{"role":"user","content":"hi"}],
          "stream":true,
          "top_k":40,
          "thinking":{"type":"adaptive"},
          "metadata":{"user_id":"session"},
          "stop_sequences":["stop"],
          "service_tier":"auto"
        }
        """;

        var decoded = _compiler.Decode(ProtocolDialect.AnthropicMessages, body);

        Assert.IsTrue(decoded.Request.RequiredCapabilities.HasFlag(GatewayCapability.Streaming));
        Assert.IsTrue(decoded.Request.RequiredCapabilities.HasFlag(GatewayCapability.Reasoning));
        Assert.IsFalse(decoded.Request.RequiredCapabilities.HasFlag(GatewayCapability.AnthropicPassthrough));
        Assert.IsTrue(decoded.Request.PreferredCapabilities.HasFlag(GatewayCapability.AnthropicPassthrough));
        Assert.AreEqual(40, decoded.Request.Options.TopK);
        Assert.AreEqual(true, decoded.Request.Options.Thinking);
    }

    [TestMethod]
    public void AnthropicUnknownContentBlock_RemainsHardPassthroughRequirement()
    {
        const string body = """
        {
          "model":"virtual",
          "max_tokens":100,
          "messages":[{"role":"user","content":[{"type":"document","source":{"type":"text","data":"important"}}]}]
        }
        """;

        var decoded = _compiler.Decode(ProtocolDialect.AnthropicMessages, body);

        Assert.IsTrue(decoded.Request.RequiredCapabilities.HasFlag(GatewayCapability.AnthropicPassthrough));
        Assert.AreEqual(GatewayCapability.None, decoded.Request.PreferredCapabilities);
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

    [TestMethod]
    [DataRow(ProtocolDialect.OllamaNative, BackendProtocol.OllamaNative)]
    [DataRow(ProtocolDialect.OllamaNative, BackendProtocol.OpenAiChatCompletions)]
    [DataRow(ProtocolDialect.OllamaNative, BackendProtocol.OpenAiResponses)]
    [DataRow(ProtocolDialect.OpenAiChatCompletions, BackendProtocol.OllamaNative)]
    [DataRow(ProtocolDialect.OpenAiChatCompletions, BackendProtocol.OpenAiChatCompletions)]
    [DataRow(ProtocolDialect.OpenAiChatCompletions, BackendProtocol.OpenAiResponses)]
    [DataRow(ProtocolDialect.OpenAiResponses, BackendProtocol.OllamaNative)]
    [DataRow(ProtocolDialect.OpenAiResponses, BackendProtocol.OpenAiChatCompletions)]
    [DataRow(ProtocolDialect.OpenAiResponses, BackendProtocol.OpenAiResponses)]
    public async Task StructuredOutput_JsonMode_UsesTargetDialect(ProtocolDialect source, BackendProtocol target)
    {
        var decoded = _compiler.Decode(source, StructuredOutputBody(source, schema: false).ToJsonString());
        Assert.IsTrue(decoded.Request.RequiredCapabilities.HasFlag(GatewayCapability.StructuredOutput));
        var backend = Backend(target == BackendProtocol.OllamaNative ? ProviderType.Ollama : ProviderType.OpenAI, "physical");
        backend.Protocol = target;

        using var request = _compiler.CreateProviderRequest(decoded, Model(), backend);
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
        var format = TargetFormat(body, target);

        Assert.IsTrue(JsonNode.DeepEquals(
            target == BackendProtocol.OllamaNative ? JsonValue.Create("json") : JsonNode.Parse("""{"type":"json_object"}"""),
            format), body.ToJsonString());
    }

    [TestMethod]
    [DataRow(ProtocolDialect.OllamaNative, BackendProtocol.OllamaNative)]
    [DataRow(ProtocolDialect.OllamaNative, BackendProtocol.OpenAiChatCompletions)]
    [DataRow(ProtocolDialect.OllamaNative, BackendProtocol.OpenAiResponses)]
    [DataRow(ProtocolDialect.OpenAiChatCompletions, BackendProtocol.OllamaNative)]
    [DataRow(ProtocolDialect.OpenAiChatCompletions, BackendProtocol.OpenAiChatCompletions)]
    [DataRow(ProtocolDialect.OpenAiChatCompletions, BackendProtocol.OpenAiResponses)]
    [DataRow(ProtocolDialect.OpenAiResponses, BackendProtocol.OllamaNative)]
    [DataRow(ProtocolDialect.OpenAiResponses, BackendProtocol.OpenAiChatCompletions)]
    [DataRow(ProtocolDialect.OpenAiResponses, BackendProtocol.OpenAiResponses)]
    public async Task StructuredOutput_Schema_PreservesSchemaAndTargetWrapper(ProtocolDialect source, BackendProtocol target)
    {
        var decoded = _compiler.Decode(source, StructuredOutputBody(source, schema: true).ToJsonString());
        var backend = Backend(target == BackendProtocol.OllamaNative ? ProviderType.Ollama : ProviderType.OpenAI, "physical");
        backend.Protocol = target;

        using var request = _compiler.CreateProviderRequest(decoded, Model(), backend);
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
        var format = TargetFormat(body, target);
        Assert.IsNotNull(format);
        if (target == BackendProtocol.OllamaNative)
        {
            Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse(OutputSchema), format), body.ToJsonString());
            return;
        }

        Assert.AreEqual("json_schema", format["type"]?.ToString());
        var wrapper = target == BackendProtocol.OpenAiChatCompletions ? format["json_schema"] : format;
        Assert.IsNotNull(wrapper);
        Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse(OutputSchema), wrapper["schema"]), body.ToJsonString());
        Assert.AreEqual(source == ProtocolDialect.OllamaNative ? "response" : "translation", wrapper["name"]?.ToString());
        if (source != ProtocolDialect.OllamaNative) Assert.AreEqual(true, wrapper["strict"]?.GetValue<bool>());
    }

    [TestMethod]
    [DataRow(ProtocolDialect.OllamaNative, BackendProtocol.OllamaNative)]
    [DataRow(ProtocolDialect.OllamaNative, BackendProtocol.OpenAiChatCompletions)]
    [DataRow(ProtocolDialect.OllamaNative, BackendProtocol.OpenAiResponses)]
    [DataRow(ProtocolDialect.OpenAiChatCompletions, BackendProtocol.OllamaNative)]
    [DataRow(ProtocolDialect.OpenAiChatCompletions, BackendProtocol.OpenAiChatCompletions)]
    [DataRow(ProtocolDialect.OpenAiChatCompletions, BackendProtocol.OpenAiResponses)]
    [DataRow(ProtocolDialect.OpenAiResponses, BackendProtocol.OllamaNative)]
    [DataRow(ProtocolDialect.OpenAiResponses, BackendProtocol.OpenAiChatCompletions)]
    [DataRow(ProtocolDialect.OpenAiResponses, BackendProtocol.OpenAiResponses)]
    public async Task StructuredOutput_Unspecified_DoesNotAddFormat(ProtocolDialect source, BackendProtocol target)
    {
        var input = StructuredOutputBody(source, schema: false);
        input.Remove("format");
        input.Remove("response_format");
        input.Remove("text");
        var decoded = _compiler.Decode(source, input.ToJsonString());
        Assert.IsFalse(decoded.Request.RequiredCapabilities.HasFlag(GatewayCapability.StructuredOutput));
        var backend = Backend(target == BackendProtocol.OllamaNative ? ProviderType.Ollama : ProviderType.OpenAI, "physical");
        backend.Protocol = target;

        using var request = _compiler.CreateProviderRequest(decoded, Model(), backend);
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
        Assert.IsNull(TargetFormat(body, target));
    }

    private const string OutputSchema = """{"type":"object","properties":{"schema":{"type":"string"}},"required":["schema"],"additionalProperties":false,"schema":{"description":"Preserve this schema extension too."}}""";

    private static JsonObject StructuredOutputBody(ProtocolDialect source, bool schema)
    {
        var body = JsonNode.Parse("""{"model":"virtual","messages":[{"role":"user","content":"Translate and return JSON."}],"stream":false}""")!.AsObject();
        JsonNode format = schema
            ? new JsonObject { ["type"] = "json_schema", ["name"] = "translation", ["schema"] = JsonNode.Parse(OutputSchema), ["strict"] = true }
            : new JsonObject { ["type"] = "json_object" };
        switch (source)
        {
            case ProtocolDialect.OllamaNative:
                body["format"] = schema ? JsonNode.Parse(OutputSchema) : JsonValue.Create("json");
                break;
            case ProtocolDialect.OpenAiChatCompletions:
                body["response_format"] = schema
                    ? new JsonObject { ["type"] = "json_schema", ["json_schema"] = new JsonObject { ["name"] = "translation", ["schema"] = JsonNode.Parse(OutputSchema), ["strict"] = true } }
                    : format;
                break;
            case ProtocolDialect.OpenAiResponses:
                body.Remove("messages");
                body["input"] = "Translate and return JSON.";
                body["text"] = new JsonObject { ["format"] = format };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(source), source, null);
        }
        return body;
    }

    private static JsonNode? TargetFormat(JsonObject body, BackendProtocol target) => target switch
    {
        BackendProtocol.OllamaNative => body["format"],
        BackendProtocol.OpenAiChatCompletions => body["response_format"],
        BackendProtocol.OpenAiResponses => body["text"]?["format"],
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, null)
    };

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
