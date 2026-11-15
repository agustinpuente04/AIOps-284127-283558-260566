using PharmaGo.PharmacyService.Factory;
using PharmaGo.PharmacyService.Filters;
using System.Diagnostics.CodeAnalysis;
using Instrumentation;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.SuppressEfAndSqlClientLogs();

// Apagado ordenado: al recibir SIGTERM (reemplazo del pod) se terminan las requests
// en curso. preStop (10s) + 25s < terminationGracePeriodSeconds (40s) del Deployment.
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(25));

builder.Services.AddHttpContextAccessor();
builder.Services.RegisterBusinessLogicServices(builder.Configuration);
builder.Services.RegisterDataAccessServices(builder.Configuration);
builder.Services.AddControllers(options => options.Filters.Add(typeof(ExceptionFilter)));

builder.Services.AddControllers();

builder.Services.AddHealthChecks();

builder.Services.AddPharmaGoOpenTelemetryMetrics("PharmaGo.PharmacyService");

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddCors(options =>
{
    options.AddPolicy("MyAllowedOrigins",
        policy =>
        {
            policy.WithOrigins("*")
                .AllowAnyHeader()
                .AllowAnyMethod();
        });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("MyAllowedOrigins");
app.UseServiceCorrelationScope();

app.UseAuthorization();

app.MapHealthChecks("/health");
app.UseMetricsMiddleware();

app.MapControllers();
app.MapPrometheusScrapingEndpoint();

app.Run();

[ExcludeFromCodeCoverage]
public partial class Program { }

