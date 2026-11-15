using Instrumentation;
using Polly;
using Polly.Extensions.Http;

namespace PharmaGo.Common.Resilience
{
    public static class ResiliencePolicies
    {
        private static readonly Random _jitterer = new Random();

        /// <param name="target">Servicio destino, se usa como label de las métricas (p. ej. "users-service").</param>
        public static IAsyncPolicy<HttpResponseMessage> GetRetryWithJitterPolicy(string target)
        {
            return HttpPolicyExtensions
                .HandleTransientHttpError()
                .WaitAndRetryAsync(
                    retryCount: 3,
                    sleepDurationProvider: retryAttempt =>
                    {
                        var exponential = TimeSpan.FromSeconds(Math.Pow(2, retryAttempt));
                        var jitter = TimeSpan.FromMilliseconds(_jitterer.Next(0, 1000));
                        return exponential + jitter;
                    },
                    onRetry: (_, _, _, _) => ResilienceMetrics.RecordRetry(target));
        }

        /// <param name="target">Servicio destino, se usa como label de las métricas (p. ej. "users-service").</param>
        public static IAsyncPolicy<HttpResponseMessage> GetCircuitBreakerPolicy(string target)
        {
            ResilienceMetrics.Register(target);
            return HttpPolicyExtensions
                .HandleTransientHttpError()
                .CircuitBreakerAsync(
                    handledEventsAllowedBeforeBreaking: 3,
                    durationOfBreak: TimeSpan.FromSeconds(30),
                    onBreak: (_, _) => ResilienceMetrics.RecordBreak(target),
                    onReset: () => ResilienceMetrics.RecordReset(target),
                    onHalfOpen: () => ResilienceMetrics.RecordHalfOpen(target));
        }
    }
}
