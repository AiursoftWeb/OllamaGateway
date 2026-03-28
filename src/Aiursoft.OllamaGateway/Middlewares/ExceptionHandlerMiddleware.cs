using Aiursoft.OllamaGateway.Models;
using System.Text.Json;

namespace Aiursoft.OllamaGateway.Middlewares;

public class ExceptionHandlerMiddleware(
    RequestDelegate next,
    ILogger<ExceptionHandlerMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OllamaGatewayException ex)
        {
            await HandleExceptionAsync(context, ex.Message, ex.StatusCode);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception occurred.");
            await HandleExceptionAsync(context, "An internal server error occurred.", 500);
        }
    }

    private static async Task HandleExceptionAsync(HttpContext context, string message, int statusCode)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = statusCode;
        
        // If it's an API request, return JSON.
        if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/v1"))
        {
            context.Response.ContentType = "application/json";
            var result = JsonSerializer.Serialize(new { error = message });
            await context.Response.WriteAsync(result);
        }
        else
        {
            // For other requests, we might want to let the default error handler take over or redirect.
            // But since we caught it, we should at least provide some content if it's not JSON.
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync(message);
        }
    }
}

public static class ExceptionHandlerMiddlewareExtensions
{
    public static IApplicationBuilder UseOllamaExceptionHandler(this IApplicationBuilder app)
    {
        return app.UseMiddleware<ExceptionHandlerMiddleware>();
    }
}
