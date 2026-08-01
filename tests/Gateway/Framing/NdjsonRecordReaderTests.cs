using System.Text;
using Aiursoft.OllamaGateway.Gateway.Framing;

namespace Aiursoft.OllamaGateway.Tests.Gateway.Framing;

[TestClass]
public class NdjsonRecordReaderTests
{
    [TestMethod]
    public async Task SkipsEmptyLinesAndPreservesJsonRecords()
    {
        const string input = "{\"value\":1}\n\n  \n{\"value\":2}\n";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(input));
        var records = new List<string>();

        await foreach (var record in NdjsonRecordReader.ReadAsync(stream))
        {
            records.Add(record);
        }

        CollectionAssert.AreEqual(
            new[] { "{\"value\":1}", "{\"value\":2}" },
            records);
    }
}
