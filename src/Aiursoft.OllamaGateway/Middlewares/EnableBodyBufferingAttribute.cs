using Microsoft.AspNetCore.Mvc.Filters;

namespace Aiursoft.OllamaGateway.Middlewares;

/// <summary>
/// Enables request body buffering before model binding so the raw body can be re-read later.
/// Required in ProxyController.Chat() so the Ollama backend path can forward the raw JSON
/// (preserving unknown fields like tools, tool_calls, tool_choice, tool_call_id) instead of
/// re-serializing the typed model which drops those fields.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class EnableBodyBufferingAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        context.HttpContext.Request.EnableBuffering();
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
