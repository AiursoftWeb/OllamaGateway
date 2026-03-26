using System.Diagnostics;
using System.Text;
using Aiursoft.OllamaGateway.Services.Clickhouse;
using Aiursoft.OllamaGateway.Models;

namespace Aiursoft.OllamaGateway.Middlewares;

public class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, RequestLogContext logContext, ClickhouseDbContext clickhouseDbContext)
    {
        var sw = Stopwatch.StartNew();
        var request = context.Request;

        request.EnableBuffering();

        var method = request.Method;
        var path = request.Path + request.QueryString;

        // Skip logging for internal error pages to avoid duplicate logs when ExceptionHandler re-executes
        if (request.Path.StartsWithSegments("/Error"))
        {
            await next(context);
            return;
        }

        logContext.Log.IP = context.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
        logContext.Log.Method = method;
        logContext.Log.Path = path;
        logContext.Log.UserAgent = request.Headers.UserAgent.ToString();
        logContext.Log.TraceId = context.TraceIdentifier;

        var body = string.Empty;
        if (request.ContentLength > 0)
        {
            request.Body.Position = 0;
            using var reader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            body = await reader.ReadToEndAsync();
            request.Body.Position = 0;
        }

        var loggedBody = body.Length > 500 ? body.Substring(0, 500) + "... [truncated]" : body;
        logger.LogInformation("→ HTTP {Method} {Path}  Body: {Body}", method, path, loggedBody);
        
        try
        {
            await next(context);
        }
        catch (Exception e)
        {
            logContext.Log.Answer = e.ToString();
            logContext.Log.StatusCode = 500;
            logContext.Log.Success = false;
            throw;
        }
        finally
        {
            if (logContext.Log.StatusCode == 0 || logContext.Log.StatusCode == 200) // update if not overridden
            {
                logContext.Log.StatusCode = context.Response.StatusCode;
            }
            if (logContext.Log.StatusCode >= 400)
            {
                logContext.Log.Success = false;
            }
            if (logContext.Log.Duration == 0)
            {
                logContext.Log.Duration = sw.Elapsed.TotalMilliseconds;
            }

            if (clickhouseDbContext.Enabled && clickhouseDbContext.RequestLogs != null)
            {
                clickhouseDbContext.RequestLogs.Add(logContext.Log);
                await clickhouseDbContext.SaveChangesAsync();
            }
        }
    }
}

public static class RequestLoggingMiddlewareExtensions
{
    public static IApplicationBuilder UseRequestLogging(this IApplicationBuilder app)
    {
        return app.UseMiddleware<RequestLoggingMiddleware>();
    }
}