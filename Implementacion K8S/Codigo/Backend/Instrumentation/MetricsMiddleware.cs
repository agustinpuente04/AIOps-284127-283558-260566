using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using InstrumentationInterface;

namespace Instrumentation
{
    public class MetricsMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ICustomMetrics _metrics;
        private readonly ILogger<MetricsMiddleware> _logger;

        public MetricsMiddleware(RequestDelegate next, ICustomMetrics metrics, ILogger<MetricsMiddleware> logger)
        {
            _next = next;
            _metrics = metrics;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var stopwatch = Stopwatch.StartNew();
            var endpoint = context.Request.Path.Value ?? "/";
            var method = context.Request.Method;

            try
            {
                await _next(context);
                stopwatch.Stop();

                var statusCode = context.Response.StatusCode;
                _metrics.RecordHttpRequest(endpoint, method, statusCode);
                _metrics.RecordEndpointDuration(endpoint, method, stopwatch.Elapsed.TotalMilliseconds);

                _logger.LogInformation("HTTP {Method} {Endpoint} responded {StatusCode} in {ElapsedMs} ms", 
                    method, endpoint, statusCode, stopwatch.Elapsed.TotalMilliseconds);

                if (statusCode >= 400)
                {
                    var errorType = statusCode >= 500 ? "server_error" : "client_error";
                    _metrics.RecordError(endpoint, errorType);
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _metrics.RecordHttpRequest(endpoint, method, 500);
                _metrics.RecordError(endpoint, "unhandled_exception");
                _logger.LogError(ex, "HTTP {Method} {Endpoint} failed with exception", method, endpoint);
                throw;
            }
        }
    }

    public static class MetricsMiddlewareExtensions
    {
        public static IApplicationBuilder UseMetricsMiddleware(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<MetricsMiddleware>();
        }
    }
}
