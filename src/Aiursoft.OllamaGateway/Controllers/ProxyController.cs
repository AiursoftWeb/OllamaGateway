using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Authorization;
using Aiursoft.OllamaGateway.Models;
using Aiursoft.OllamaGateway.Services.Proxy.Handlers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Aiursoft.OllamaGateway.Controllers;

[Route("api")]
[AllowAnonymous]
[RequiresUserOrApiKeyAuth]
public class ProxyController(
    IOllamaChatHandler chatHandler,
    IOllamaEmbeddingHandler embeddingHandler,
    IModelsInfoService modelsInfoService) : ControllerBase
{
    [HttpPost("chat")]
    public async Task Chat([FromBody] OllamaRequestModel input)
    {
        await chatHandler.ProxyChatAsync(input, HttpContext);
    }

    [HttpPost("generate")]
    public async Task Generate([FromBody] OllamaRequestModel input)
    {
        await chatHandler.ProxyCompletionsAsync(input, HttpContext);
    }

    [HttpPost("embed")]
    public async Task Embed()
    {
        var inputNode = await JsonNode.ParseAsync(Request.Body);
        if (inputNode == null)
        {
            Response.StatusCode = 400;
            await Response.WriteAsync("Request body is empty or invalid JSON.");
            return;
        }
        await embeddingHandler.ProxyEmbedAsync(inputNode.AsObject(), HttpContext);
    }

    [HttpGet("tags")]
    public async Task<IActionResult> Tags()
    {
        return await modelsInfoService.GetTagsAsync();
    }

    [HttpGet("ps")]
    public async Task<IActionResult> Ps()
    {
        return await modelsInfoService.GetPsAsync();
    }

    [HttpGet("version")]
    public async Task<IActionResult> Version()
    {
        return await modelsInfoService.GetVersionAsync();
    }
}
