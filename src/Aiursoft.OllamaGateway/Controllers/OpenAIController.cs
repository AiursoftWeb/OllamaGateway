using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Authorization;
using Aiursoft.OllamaGateway.Services.Proxy.Handlers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Aiursoft.OllamaGateway.Controllers;

[AllowAnonymous]
[RequiresUserOrApiKeyAuth]
public class OpenAIController(
    IOpenAIChatHandler chatHandler,
    IOllamaEmbeddingHandler embeddingHandler,
    IModelsInfoService modelsInfoService) : ControllerBase
{
    [HttpPost("/v1/chat/completions")]
    public async Task Chat()
    {
        var bodyStr = await new StreamReader(Request.Body).ReadToEndAsync();
        var clientJson = JsonNode.Parse(bodyStr)?.AsObject();
        if (clientJson == null)
        {
            Response.StatusCode = 400;
            await Response.WriteAsync("Invalid JSON body.");
            return;
        }

        await chatHandler.ProxyOpenAIChatAsync(clientJson, HttpContext);
    }

    [HttpPost("/v1/embeddings")]
    public async Task Embeddings()
    {
        var bodyStr = await new StreamReader(Request.Body).ReadToEndAsync();
        var clientJson = JsonNode.Parse(bodyStr)?.AsObject();
        if (clientJson == null)
        {
            Response.StatusCode = 400;
            await Response.WriteAsync("Invalid JSON body.");
            return;
        }

        // OpenAI format: { "model": "...", "input": "..." }
        // Ollama format: { "model": "...", "input": "..." } (same)
        await embeddingHandler.ProxyEmbedAsync(clientJson, HttpContext);
    }

    [HttpGet("/v1/models")]
    public async Task<IActionResult> Models()
    {
        return await modelsInfoService.GetOpenAIModelsAsync();
    }
}
