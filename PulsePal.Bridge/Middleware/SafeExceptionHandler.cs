using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using PulsePal.Bridge.Services;

namespace PulsePal.Bridge.Middleware;

internal sealed class SafeExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var status = exception switch
        {
            BridgeRequestException error => error.StatusCode,
            BadHttpRequestException error => error.StatusCode,
            System.Text.Json.JsonException => 400,
            _ => 500
        };
        await WriteProblem(context, status, ct);
        return true;
    }

    internal static Task WriteProblem(HttpContext context, int status, CancellationToken ct)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status,
            Title = status switch
            {
                400 => "Invalid request",
                401 => "Unauthorized",
                404 => "Not found",
                409 => "Pairing unavailable",
                413 => "Request too large",
                415 => "Unsupported media type",
                429 => "Too many requests",
                _ => "Request failed"
            },
            Type = "about:blank"
        }, options: null, contentType: "application/problem+json", cancellationToken: ct);
    }
}
