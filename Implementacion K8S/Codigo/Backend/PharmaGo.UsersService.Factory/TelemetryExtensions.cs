using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using OpenTelemetry.Resources;
using OpenTelemetry.Exporter;
using System.Diagnostics;

namespace PharmaGo.UsersService.Factory
{
    public static class OpenTelemetryExtensions
    {
        public static IServiceCollection AddPharmaGoOpenTelemetryMetrics(this IServiceCollection services, string serviceName = "PharmaGo.UsersService")
        {
            services.AddOpenTelemetry()
                    .ConfigureResource(r => r.AddService(serviceName))
                    .WithMetrics(metricsBuilder => {
                        metricsBuilder
                            .AddAspNetCoreInstrumentation()
                            .AddHttpClientInstrumentation()
                            .AddMeter("PharmaGo.CustomMetrics")
                            .AddPrometheusExporter()
                            .AddOtlpExporter(options =>
                            {
                                options.Endpoint = new Uri("http://otlp-collector:4317");
                                options.Protocol = OtlpExportProtocol.Grpc;
                            });
                    })
                    .WithTracing(tracingBuilder => {
                        tracingBuilder
                            .AddAspNetCoreInstrumentation(opts => opts.RecordException = true)
                            .AddHttpClientInstrumentation()
                            .AddOtlpExporter(options =>
                            {
                                options.Endpoint = new Uri("http://otlp-collector:4317");
                                options.Protocol = OtlpExportProtocol.Grpc;
                            });
                    });

            return services;
        }
    }
}
