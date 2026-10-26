using System.Diagnostics.CodeAnalysis;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using AspNetCoreRateLimit;
using PharmaGo.ApiGateway.Middleware;
using Instrumentation;
using InstrumentationInterface;
using Yarp.ReverseProxy.Transforms;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMemoryCache();
builder.Services.AddHealthChecks();

var rateLimitMode = builder.Configuration["RateLimiting:Mode"] ?? "IP";

if (rateLimitMode.Equals("IP", StringComparison.OrdinalIgnoreCase))
{
    // Rate limiting por IP (útil para endpoints públicos)
    builder.Services.Configure<IpRateLimitOptions>(builder.Configuration.GetSection("IpRateLimiting"));
    builder.Services.Configure<IpRateLimitPolicies>(builder.Configuration.GetSection("IpRateLimitPolicies"));
    builder.Services.AddInMemoryRateLimiting();
    builder.Services.AddSingleton<IRateLimitConfiguration, RateLimitConfiguration>();
}

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddTransforms(transformBuilder =>
    {
        transformBuilder.AddRequestTransform(context =>
        {
            if (context.HttpContext.Items.TryGetValue(CorrelationIdMiddlewareExtensions.HttpContextItemKey, out var value)
                && value is string correlationId
                && !string.IsNullOrEmpty(correlationId))
            {
                context.ProxyRequest.Headers.Remove("X-Correlation-ID");
                context.ProxyRequest.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId);
            }
            return default;
        });
    });

builder.Services.AddSingleton<ICustomMetrics, CustomMetrics>();

builder.Services.AddOpenTelemetry()
    .WithMetrics(metricsBuilder => 
    {
        metricsBuilder
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("PharmaGo.ApiGateway"))
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddMeter("PharmaGo.CustomMetrics")
            .AddPrometheusExporter();
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddCors(options =>
{
    options.AddPolicy("MyAllowedOrigins",
        policy =>
        {
            policy.SetIsOriginAllowed(_ => true)
                .AllowAnyHeader()
                .AllowAnyMethod();
        });
});

var app = builder.Build();

app.UseCors("MyAllowedOrigins");
app.UseGatewayCorrelationId();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMetricsMiddleware();

if (rateLimitMode.Equals("IP", StringComparison.OrdinalIgnoreCase))
{
    app.UseIpRateLimiting();
}
else if (rateLimitMode.Equals("User", StringComparison.OrdinalIgnoreCase))
{
    app.UseUserRateLimit();
}

app.UseAuthorization();

app.MapReverseProxy();
app.MapHealthChecks("/health");
app.MapPrometheusScrapingEndpoint();

app.Run();

[ExcludeFromCodeCoverage]
public partial class Program { }
