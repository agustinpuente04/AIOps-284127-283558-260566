using Polly;
using Polly.Extensions.Http;

namespace PharmaGo.Common.Resilience
{
    public static class ResiliencePolicies
    {
        private static readonly Random _jitterer = new Random();

        public static IAsyncPolicy<HttpResponseMessage> GetRetryWithJitterPolicy()
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
                    });
        }

        public static IAsyncPolicy<HttpResponseMessage> GetCircuitBreakerPolicy()
        {
            return HttpPolicyExtensions
                .HandleTransientHttpError()
                .CircuitBreakerAsync(
                    handledEventsAllowedBeforeBreaking: 3,
                    durationOfBreak: TimeSpan.FromSeconds(30));
        }
    }
}
