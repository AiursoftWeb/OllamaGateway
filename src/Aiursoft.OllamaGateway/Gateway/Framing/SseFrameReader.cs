using System.Runtime.CompilerServices;

namespace Aiursoft.OllamaGateway.Gateway.Framing;

/// <summary>
/// Reads complete Server-Sent Events rather than treating individual lines as events.
/// </summary>
public static class SseFrameReader
{
    public static async IAsyncEnumerable<SseFrame> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var dataLines = new List<string>();
        var comments = new List<string>();
        string? eventType = null;
        string? id = null;
        int? retry = null;
        var hasFields = false;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (hasFields)
                {
                    yield return CreateFrame(eventType, dataLines, id, retry, comments);
                    dataLines.Clear();
                    comments.Clear();
                    eventType = null;
                    id = null;
                    retry = null;
                    hasFields = false;
                }

                continue;
            }

            hasFields = true;
            if (line[0] == ':')
            {
                comments.Add(RemoveOptionalLeadingSpace(line[1..]));
                continue;
            }

            var colonIndex = line.IndexOf(':');
            var field = colonIndex < 0 ? line : line[..colonIndex];
            var value = colonIndex < 0
                ? string.Empty
                : RemoveOptionalLeadingSpace(line[(colonIndex + 1)..]);

            switch (field)
            {
                case "event":
                    eventType = value;
                    break;
                case "data":
                    dataLines.Add(value);
                    break;
                case "id" when !value.Contains('\0'):
                    id = value;
                    break;
                case "retry" when int.TryParse(value, out var parsedRetry):
                    retry = parsedRetry;
                    break;
            }
        }

        if (hasFields)
        {
            yield return CreateFrame(eventType, dataLines, id, retry, comments);
        }
    }

    private static SseFrame CreateFrame(
        string? eventType,
        IReadOnlyList<string> dataLines,
        string? id,
        int? retry,
        IReadOnlyList<string> comments)
    {
        return new SseFrame(eventType, string.Join('\n', dataLines), id, retry, comments.ToArray());
    }

    private static string RemoveOptionalLeadingSpace(string value)
    {
        return value.StartsWith(' ') ? value[1..] : value;
    }
}
