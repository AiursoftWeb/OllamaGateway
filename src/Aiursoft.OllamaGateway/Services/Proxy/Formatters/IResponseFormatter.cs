using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Services.Proxy.Formatters;

public interface IResponseFormatter
{
    Task FormatResponseAsync(HttpResponseMessage upstreamResponse, HttpContext context, VirtualModel virtualModel, bool isStream, ModelType type);
}
