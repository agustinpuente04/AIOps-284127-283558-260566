using System.Diagnostics.CodeAnalysis;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using OpenTelemetry.Resources;
using AspNetCoreRateLimit;
using PharmaGo.ApiGateway.RateLimiting;
using Instrumentation;
using InstrumentationInterface;
using Yarp.ReverseProxy.Transforms;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMemoryCache();
builder.Services.AddHealthChecks();

// Apagado ordenado: al recibir SIGTERM (reemplazo del pod) se terminan las requests
// en curso. preStop (10s) + 25s < terminationGracePeriodSeconds (40s) del Deployment.
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(25));

var rateLimitMode = builder.Configuration["RateLimiting:Mode"] ?? "TokenBucket";

if (rateLimitMode.Equals("TokenBucket", StringComparison.OrdinalIgnoreCase))
{
    // Token bucket con cubos por identidad (usuario autenticado / IP) + protección de login. Ver RateLimiting/.
    builder.Services.AddTokenBucketRateLimiting(builder.Configuration);
}
else if (rateLimitMode.Equals("IP", StringComparison.OrdinalIgnoreCase))
{
    // Modo anterior (ventana fija por IP con AspNetCoreRateLimit). Se conserva solo como vía de retroceso.
    builder.Services.Configure<IpRateLimitOptions>(builder.Configuration.GetSection("IpRateLimiting"));
    builder.Services.Configure<IpRateLimitPolicies>(builder.Configuration.GetSection("IpRateLimitPolicies"));
    builder.Services.AddInMemoryRateLimiting();
    builder.Services.AddSingleton<IRateLimitConfiguration, RateLimitConfiguration>();
}

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    // Las conexiones keep-alive quedan fijadas a un pod. Con un idle timeout menor que el
    // preStop (10s) de los servicios, caducan antes de que el pod viejo se apague.
    .ConfigureHttpClient((context, handler) =>
    {
        handler.PooledConnectionIdleTimeout = TimeSpan.FromSeconds(5);
        handler.PooledConnectionLifetime = TimeSpan.FromSeconds(30);
    })
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

var otlpEndpoint = builder.Configuration["Otlp:Endpoint"] ?? "http://otlp-collector:4317";

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("PharmaGo.ApiGateway"))
    .WithMetrics(metricsBuilder => 
    {
        metricsBuilder
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddMeter("PharmaGo.CustomMetrics")
            .AddPrometheusExporter()
            // Mismo camino que users/pharmacy: app -> OTLP -> collector -> Prometheus.
            // Cada réplica exporta con su propio service.instance.id (el scrape directo
            // de /metrics vía Service mezcla réplicas).
            .AddOtlpExporter(opts => opts.Endpoint = new Uri(otlpEndpoint));
    })
    .WithTracing(tracingBuilder =>
    {
        tracingBuilder
            .AddAspNetCoreInstrumentation(opts => opts.RecordException = true)
            .AddHttpClientInstrumentation()
            .AddOtlpExporter(opts => opts.Endpoint = new Uri(otlpEndpoint));
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

var tokenBucketMode = rateLimitMode.Equals("TokenBucket", StringComparison.OrdinalIgnoreCase);

// Primero: reemplaza la IP del socket por la real SOLO si el peer es un pod de nginx.
if (tokenBucketMode) app.UseClientIp();

app.UseCors("MyAllowedOrigins");
app.UseGatewayCorrelationId();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMetricsMiddleware();

if (tokenBucketMode)
{
    app.UseTokenBucketRateLimiting();
}
else if (rateLimitMode.Equals("IP", StringComparison.OrdinalIgnoreCase))
{
    app.UseIpRateLimiting();
}

app.UseAuthorization();

app.MapHealthChecks("/health");
app.MapPrometheusScrapingEndpoint();
app.MapReverseProxy();

app.Run();

[ExcludeFromCodeCoverage]
public partial class Program { }
