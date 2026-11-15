using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using InstrumentationInterface;

namespace Instrumentation
{
    public class CustomMetrics : ICustomMetrics, IDisposable
    {
        private readonly Meter _meter;
        private readonly Counter<long> _loginInvocationsCounter;
        private readonly Counter<long> _httpRequestsCounter;
        private readonly Counter<long> _httpErrorsCounter;
        private readonly Histogram<double> _requestDurationHistogram;
        private readonly Histogram<double> _endpointDurationHistogram;
        private readonly UpDownCounter<long> _requestsInFlight;
        private readonly Counter<long> _loginsCounter;
        private readonly Counter<long> _purchasesCounter;
        private readonly Histogram<double> _purchaseAmountHistogram;
        private readonly Counter<long> _stockRequestsCounter;
        private int _activeUserCount;

        public CustomMetrics()
        {
            _meter = new Meter("PharmaGo.CustomMetrics");

            _loginInvocationsCounter = _meter.CreateCounter<long>(
                "login_invocations",
                unit: "1",
                description: "Counts the number of login invocations"
            );

            _httpRequestsCounter = _meter.CreateCounter<long>(
                "pharmago_http_requests_total",
                unit: "1",
                description: "Total number of HTTP requests by endpoint, method, and status code"
            );

            _httpErrorsCounter = _meter.CreateCounter<long>(
                "pharmago_http_errors_total",
                unit: "1",
                description: "Total number of HTTP errors by endpoint and error type"
            );

            _requestDurationHistogram = _meter.CreateHistogram<double>(
                "request_duration",
                unit: "ms",
                description: "Records the duration of requests in milliseconds"
            );

            _endpointDurationHistogram = _meter.CreateHistogram<double>(
                "pharmago_http_request_duration_milliseconds",
                unit: "ms",
                description: "HTTP request duration in milliseconds by endpoint and method"
            );

            _meter.CreateObservableGauge(
                "active_user_count",
                () => new Measurement<int>[] { new Measurement<int>(_activeUserCount) },
                unit: "1",
                description: "The current number of active users"
            );

            // --- Saturación ---
            _requestsInFlight = _meter.CreateUpDownCounter<long>(
                "pharmago_http_requests_in_flight",
                unit: "1",
                description: "HTTP requests currently being processed"
            );

            // --- Negocio ---
            _loginsCounter = _meter.CreateCounter<long>(
                "pharmago_logins_total",
                unit: "1",
                description: "Login attempts by outcome (success | failed)"
            );

            _purchasesCounter = _meter.CreateCounter<long>(
                "pharmago_purchases_total",
                unit: "1",
                description: "Purchase creations by outcome (success | failed)"
            );

            _purchaseAmountHistogram = _meter.CreateHistogram<double>(
                "pharmago_purchase_amount",
                // Unidad entre llaves = anotación: el exporter Prometheus del collector no la
                // agrega como sufijo (con "1" quedaría pharmago_purchase_amount_ratio_*).
                unit: "{currency}",
                description: "Total amount of successfully created purchases"
            );

            _stockRequestsCounter = _meter.CreateCounter<long>(
                "pharmago_stock_requests_total",
                unit: "1",
                description: "Stock request operations by action (create | approve | reject) and outcome"
            );
        }

        public void LoginInvocations(long value = 1)
        {
            Console.WriteLine("Logging invocation");
            _loginInvocationsCounter.Add(value);
        }

        public void RecordHttpRequest(string endpoint, string method, int statusCode)
        {
            var tags = new KeyValuePair<string, object?>[]
            {
                new("endpoint", endpoint),
                new("method", method),
                new("status_code", statusCode.ToString())
            };
            _httpRequestsCounter.Add(1, tags);
        }

        public void RecordError(string endpoint, string errorType)
        {
            var tags = new KeyValuePair<string, object?>[]
            {
                new("endpoint", endpoint),
                new("error_type", errorType)
            };
            _httpErrorsCounter.Add(1, tags);
        }

        public void SetActiveUserCount(int count)
        {
            _activeUserCount = count;
        }

        public void RequestDuration(double milliseconds)
        {
            _requestDurationHistogram.Record(milliseconds);
        }

        public void RecordEndpointDuration(string endpoint, string method, double milliseconds)
        {
            var tags = new KeyValuePair<string, object?>[]
            {
                new("endpoint", endpoint),
                new("method", method)
            };
            _endpointDurationHistogram.Record(milliseconds, tags);
        }

        public void ChangeRequestsInFlight(int delta)
        {
            _requestsInFlight.Add(delta);
        }

        public void RecordLogin(string outcome)
        {
            _loginsCounter.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        }

        public void RecordPurchase(string outcome, decimal amount)
        {
            var tag = new KeyValuePair<string, object?>("outcome", outcome);
            _purchasesCounter.Add(1, tag);
            if (amount > 0)
            {
                _purchaseAmountHistogram.Record((double)amount, tag);
            }
        }

        public void RecordStockRequest(string action, string outcome)
        {
            _stockRequestsCounter.Add(1,
                new KeyValuePair<string, object?>("action", action),
                new KeyValuePair<string, object?>("outcome", outcome));
        }

        public void Dispose()
        {
            _meter?.Dispose();
        }
    }
}
