using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Services;

/// <summary>
/// Holds the result of a successful backend invocation. The caller MUST dispose this
/// to release the concurrency slot (if any). The HttpResponseMessage is also disposed.
/// </summary>
public sealed class BackendInvocationResult : IAsyncDisposable
{
    private readonly IAsyncDisposable? _concurrencySlot;

    public HttpResponseMessage Response { get; }
    public VirtualModelBackend Backend { get; }

    internal BackendInvocationResult(HttpResponseMessage response, VirtualModelBackend backend, IAsyncDisposable? concurrencySlot)
    {
        Response = response;
        Backend = backend;
        _concurrencySlot = concurrencySlot;
    }

    public async ValueTask DisposeAsync()
    {
        if (_concurrencySlot != null)
            await _concurrencySlot.DisposeAsync();
        Response.Dispose();
    }
}
