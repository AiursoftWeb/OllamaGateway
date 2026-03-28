namespace Aiursoft.OllamaGateway.Services.Proxy;

public static class HttpResponseExtensions
{
    private static readonly HashSet<string> HeaderBlacklist = new(StringComparer.OrdinalIgnoreCase)
    {
        "Transfer-Encoding", "Content-Length", "Connection", "Keep-Alive", "Upgrade", "Host", "Accept-Ranges"
    };

    public static void CopyHeadersTo(this HttpResponseMessage response, HttpResponse destination)
    {
        foreach (var header in response.Headers)
        {
            if (!HeaderBlacklist.Contains(header.Key))
            {
                destination.Headers.Append(header.Key, header.Value.ToArray());
            }
        }
        foreach (var header in response.Content.Headers)
        {
            if (!HeaderBlacklist.Contains(header.Key))
            {
                destination.Headers.Append(header.Key, header.Value.ToArray());
            }
        }
    }
}
