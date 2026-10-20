using System.Diagnostics;
using Microsoft.AspNetCore.Routing;

namespace SelfService.Portal;

/// <summary>Correlates UI requests with safe route metadata without collecting bodies or browser identity.</summary>
public sealed class PortalTelemetry(RequestDelegate next, ILoggerFactory logs)
{
    readonly ILogger log = logs.CreateLogger("PlatformStudio.Requests");
    public async Task Invoke(HttpContext context)
    {
        // The OTel ASP.NET instrumentation owns the server span. Do not create a duplicate request span.
        var activity = Activity.Current;
        if (activity is not null) context.Response.Headers["X-Trace-Id"] = activity.TraceId.ToHexString();
        try { await next(context); }
        catch (Exception e)
        {
            activity?.SetTag("error.type", e.GetType().FullName);
            if (e is not OperationCanceledException) activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
        finally
        {
            // Route templates contain no user supplied identifiers; raw paths, queries and headers are excluded.
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
            if (route is not null) activity?.SetTag("http.route", route);
            activity?.SetTag("http.response.status_code", context.Response.StatusCode);
            if (context.Response.StatusCode >= 500) activity?.SetStatus(ActivityStatusCode.Error);
            log.LogInformation(new EventId(1, "RequestCompleted"), "Request completed {http.response.status_code}", context.Response.StatusCode);
        }
    }
}
