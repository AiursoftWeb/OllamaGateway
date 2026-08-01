using System.Text;
using Aiursoft.OllamaGateway.Gateway.Framing;

namespace Aiursoft.OllamaGateway.Tests.Gateway.Framing;

[TestClass]
public class SseFrameReaderTests
{
    [TestMethod]
    public async Task ReadsCompleteFramesAndJoinsDataLines()
    {
        const string input = "event: content_block_delta\n" +
                             "id: abc\n" +
                             "retry: 1500\n" +
                             ": heartbeat\n" +
                             "data: {\"first\":true}\n" +
                             "data:{\"second\":true}\n\n";

        var frames = await ReadAllAsync(input);

        Assert.HasCount(1, frames);
        Assert.AreEqual("content_block_delta", frames[0].EventType);
        Assert.AreEqual("abc", frames[0].Id);
        Assert.AreEqual(1500, frames[0].RetryMilliseconds);
        Assert.AreEqual("{\"first\":true}\n{\"second\":true}", frames[0].Data);
        CollectionAssert.AreEqual(new[] { "heartbeat" }, frames[0].Comments.ToArray());
    }

    [TestMethod]
    public async Task EmitsFinalFrameWithoutTrailingBlankLine()
    {
        var frames = await ReadAllAsync("data: [DONE]");

        Assert.HasCount(1, frames);
        Assert.AreEqual("[DONE]", frames[0].Data);
    }

    [TestMethod]
    public async Task IgnoresInvalidRetryAndNullContainingId()
    {
        var frames = await ReadAllAsync("id: invalid\0id\nretry: later\ndata: ok\n\n");

        Assert.HasCount(1, frames);
        Assert.IsNull(frames[0].Id);
        Assert.IsNull(frames[0].RetryMilliseconds);
        Assert.AreEqual("ok", frames[0].Data);
    }

    private static async Task<List<SseFrame>> ReadAllAsync(string input)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(input));
        var frames = new List<SseFrame>();
        await foreach (var frame in SseFrameReader.ReadAsync(stream))
        {
            frames.Add(frame);
        }

        return frames;
    }
}
