using System.Text;
using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Gateway.Chat;

namespace Aiursoft.OllamaGateway.Tests.Gateway.Chat;

[TestClass]
public class ChatStreamingTranslationTests
{
    private const string OpenAiSseWithSeparateUsage =
        "data: {\"id\":\"r-usage\",\"model\":\"physical\",\"choices\":[{\"delta\":{\"content\":\"answer\"},\"finish_reason\":null}]}\n\n" +
        "data: {\"id\":\"r-usage\",\"model\":\"physical\",\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}\n\n" +
        "data: {\"id\":\"r-usage\",\"model\":\"physical\",\"choices\":[],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":3,\"total_tokens\":8}}\n\n" +
        "data: [DONE]\n\n";

    [TestMethod]
    public async Task OpenAiSseWithSeparateUsage_EmitsUsageBeforeSingleCompletion()
    {
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(OpenAiSseWithSeparateUsage));
        var decoder = new OpenAiChatProviderResponseDecoder();
        var events = new List<GatewayChatEvent>();

        await foreach (var item in decoder.DecodeAsync(source, true, CancellationToken.None))
            events.Add(item);

        Assert.AreEqual("answer", string.Concat(events.OfType<GatewayTextDelta>().Select(item => item.Text)));
        Assert.AreEqual(1, events.OfType<GatewayUsageUpdated>().Count());
        Assert.AreEqual(1, events.OfType<GatewayResponseCompleted>().Count());

        var usageIndex = events.FindIndex(item => item is GatewayUsageUpdated);
        var completionIndex = events.FindIndex(item => item is GatewayResponseCompleted);
        Assert.IsTrue(usageIndex >= 0 && usageIndex < completionIndex);

        var usage = events.OfType<GatewayUsageUpdated>().Single();
        Assert.AreEqual(5L, usage.PromptTokens);
        Assert.AreEqual(3L, usage.CompletionTokens);
        Assert.AreEqual(GatewayFinishReason.Length, events.OfType<GatewayResponseCompleted>().Single().FinishReason);
    }

    [TestMethod]
    public async Task OpenAiSseWithSeparateUsage_ToOllamaNdjsonIncludesTokenCounts()
    {
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(OpenAiSseWithSeparateUsage));
        var decoder = new OpenAiChatProviderResponseDecoder();
        var writer = new OllamaChatClientResponseWriter();
        var context = Context();

        await writer.WriteTranslatedAsync(
            decoder.DecodeAsync(source, true, CancellationToken.None),
            Model(),
            true,
            context.Response,
            CancellationToken.None);

        var lines = (await Body(context)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var completed = lines
            .Select(line => JsonNode.Parse(line))
            .Where(item => item?["done"]?.GetValue<bool>() == true)
            .ToList();

        Assert.AreEqual(1, completed.Count);
        Assert.AreEqual("answer", completed[0]?["message"]?["content"]?.ToString());
        Assert.AreEqual("length", completed[0]?["done_reason"]?.ToString());
        Assert.AreEqual(5L, completed[0]?["prompt_eval_count"]?.GetValue<long>());
        Assert.AreEqual(3L, completed[0]?["eval_count"]?.GetValue<long>());
    }

    [TestMethod]
    public async Task OpenAiSseToAnthropicSse_PreservesSemanticEventOrder()
    {
        const string sse =
            "data: {\"id\":\"r1\",\"model\":\"physical\",\"choices\":[{\"delta\":{\"reasoning_content\":\"think\"},\"finish_reason\":null}]}\n\n" +
            "data:{\"id\":\"r1\",\"model\":\"physical\",\"choices\":[{\"delta\":{\"content\":\"answer\"},\"finish_reason\":null}]}\n\n" +
            "data: {\"id\":\"r1\",\"model\":\"physical\",\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"lookup\",\"arguments\":\"{\\\"q\\\":\"}}]},\"finish_reason\":null}]}\n\n" +
            "data: {\"id\":\"r1\",\"model\":\"physical\",\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\"x\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":3}}\n\n" +
            "data: [DONE]\n\n";
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        var decoder = new OpenAiChatProviderResponseDecoder();
        var writer = new AnthropicChatClientResponseWriter();
        var context = Context();

        await writer.WriteTranslatedAsync(
            decoder.DecodeAsync(source, true, CancellationToken.None),
            Model(),
            true,
            context.Response,
            CancellationToken.None);

        var output = await Body(context);
        StringAssert.Contains(output, "event: message_start");
        StringAssert.Contains(output, "\"type\":\"thinking_delta\",\"thinking\":\"think\"");
        StringAssert.Contains(output, "\"type\":\"text_delta\",\"text\":\"answer\"");
        StringAssert.Contains(output, "\"type\":\"tool_use\"");
        StringAssert.Contains(output, "\"type\":\"input_json_delta\"");
        StringAssert.Contains(output, "\"stop_reason\":\"tool_use\"");
        StringAssert.Contains(output, "\"output_tokens\":3");
        StringAssert.Contains(output, "event: message_stop");
        Assert.IsTrue(output.IndexOf("thinking_delta", StringComparison.Ordinal) < output.IndexOf("text_delta", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task OllamaNdjsonToOpenAiSse_EmitsUsageFinishAndDone()
    {
        const string ndjson =
            "{\"model\":\"physical\",\"message\":{\"role\":\"assistant\",\"thinking\":\"why\",\"content\":\"\"},\"done\":false}\n" +
            "{\"model\":\"physical\",\"message\":{\"role\":\"assistant\",\"content\":\"hello\"},\"done\":false}\n" +
            "{\"model\":\"physical\",\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true,\"done_reason\":\"stop\",\"prompt_eval_count\":7,\"eval_count\":2}\n";
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(ndjson));
        var decoder = new OllamaChatProviderResponseDecoder();
        var writer = new OpenAiChatClientResponseWriter();
        var context = Context();

        await writer.WriteTranslatedAsync(
            decoder.DecodeAsync(source, true, CancellationToken.None),
            Model(),
            true,
            context.Response,
            CancellationToken.None);

        var output = await Body(context);
        StringAssert.Contains(output, "\"model\":\"virtual\"");
        StringAssert.Contains(output, "\"reasoning_content\":\"why\"");
        StringAssert.Contains(output, "\"content\":\"hello\"");
        StringAssert.Contains(output, "\"prompt_tokens\":7");
        StringAssert.Contains(output, "\"completion_tokens\":2");
        StringAssert.Contains(output, "\"finish_reason\":\"stop\"");
        Assert.IsTrue(output.EndsWith("data: [DONE]\n\n", StringComparison.Ordinal));
    }

    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static VirtualModel Model() => new() { Name = "virtual", Type = ModelType.Chat };

    private static async Task<string> Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }
}
