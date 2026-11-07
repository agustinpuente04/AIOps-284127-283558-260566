using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Instrumentation
{
    /// <summary>
    /// Métricas de resiliencia (Polly) para las llamadas HTTP entre microservicios.
    ///
    /// Es estática porque las políticas de Polly se crean una sola vez al registrar el HttpClient
    /// (ResiliencePolicies) y el circuit breaker tiene que ser una instancia compartida: crearla por
    /// request con una factory de DI le haría perder el estado. Usa el mismo nombre de Meter que
    /// CustomMetrics, así que la toma el mismo <c>.AddMeter("PharmaGo.CustomMetrics")</c> del SDK.
    /// </summary>
    public static class ResilienceMetrics
    {
        public const double StateClosed = 0;
        public const double StateHalfOpen = 0.5;
        public const double StateOpen = 1;

        private static readonly Meter Meter = new("PharmaGo.CustomMetrics");

        private static readonly Counter<long> Retries = Meter.CreateCounter<long>(
            "pharmago_http_client_retries_total",
            unit: "1",
            description: "Retries performed by the Polly retry policy, by target service");

        private static readonly Counter<long> BreakerOpened = Meter.CreateCounter<long>(
            "pharmago_circuit_breaker_opened_total",
            unit: "1",
            description: "Times the circuit breaker opened, by target service");

        private static readonly ConcurrentDictionary<string, double> States = new();

        static ResilienceMetrics()
        {
            Meter.CreateObservableGauge(
                "pharmago_circuit_breaker_state",
                () => States.Select(kv => new Measurement<double>(kv.Value, new KeyValuePair<string, object?>("target", kv.Key))),
                // "{state}" es una anotación: evita que el collector lo renombre a *_ratio.
                unit: "{state}",
                description: "Circuit breaker state by target service: 0 closed, 0.5 half-open, 1 open");
        }

        /// <summary>Registra el target con estado "cerrado" para que la serie exista desde el arranque.</summary>
        public static void Register(string target) => States.TryAdd(target, StateClosed);

        public static void RecordRetry(string target) =>
            Retries.Add(1, new KeyValuePair<string, object?>("target", target));

        public static void RecordBreak(string target)
        {
            States[target] = StateOpen;
            BreakerOpened.Add(1, new KeyValuePair<string, object?>("target", target));
        }

        public static void RecordHalfOpen(string target) => States[target] = StateHalfOpen;

        public static void RecordReset(string target) => States[target] = StateClosed;
    }
}
