namespace Aiursoft.OllamaGateway.Models;

public class OllamaGatewayException(string message, int statusCode = 500) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public class ModelNotFoundException(string modelName) : OllamaGatewayException($"Model '{modelName}' not found in gateway.", 404);

public class NoAvailableBackendException(string modelName) : OllamaGatewayException($"No available backend for model '{modelName}'.", 503);

public class ForbiddenException(string message) : OllamaGatewayException(message, 403);
