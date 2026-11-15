using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Instrumentation;

public static class CorrelationIdMiddlewareExtensions
{
    public const string HttpContextItemKey = "YarpCorrelationId";

    public static IApplicationBuilder UseGatewayCorrelationId(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            var header = context.Request.Headers["X-Correlation-ID"].FirstOrDefault();
            var correlationId = string.IsNullOrWhiteSpace(header)
                ? Guid.NewGuid().ToString("D")
                : header.Trim();
            context.Items[HttpContextItemKey] = correlationId;
            context.Response.Headers["X-Correlation-ID"] = correlationId;

            var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
            var log = loggerFactory.CreateLogger("GatewayRequest");
            using (log.BeginScope(new Dictionary<string, object> 
            { 
                ["correlation_id"] = correlationId,
                ["service"] = "PharmaGo.ApiGateway"
            }))
            {
                await next();
            }
        });
    }

    public static IApplicationBuilder UseServiceCorrelationScope(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            var header = context.Request.Headers["X-Correlation-ID"].FirstOrDefault();
            var correlationId = string.IsNullOrWhiteSpace(header)
                ? context.TraceIdentifier
                : header.Trim();
            context.Items[HttpContextItemKey] = correlationId;

            var env = context.RequestServices.GetRequiredService<IHostEnvironment>();
            var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
            var log = loggerFactory.CreateLogger("ServiceRequest");
            using (log.BeginScope(new Dictionary<string, object> 
            { 
                ["correlation_id"] = correlationId,
                ["service"] = env.ApplicationName
            }))
            {
                await next();
            }
        });
    }
}
