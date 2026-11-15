using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InstrumentationInterface
{
    public interface ICustomMetrics
    {
            // Counters
            void LoginInvocations(long value = 1);
            
            /// <summary>
            /// Records an HTTP request with endpoint, method, and status code
            /// </summary>
            void RecordHttpRequest(string endpoint, string method, int statusCode);
            
            /// <summary>
            /// Records an error/exception for a specific endpoint
            /// </summary>
            void RecordError(string endpoint, string errorType);

            // Gauges
            void SetActiveUserCount(int count);

            // Histograms / Timings
            void RequestDuration(double milliseconds);
            
            /// <summary>
            /// Records the duration of a specific endpoint request
            /// </summary>
            void RecordEndpointDuration(string endpoint, string method, double milliseconds);

            // Saturación

            /// <summary>
            /// Suma (+1) o resta (-1) una request en curso. Se llama desde MetricsMiddleware.
            /// </summary>
            void ChangeRequestsInFlight(int delta);

            // Negocio

            /// <summary>
            /// Registra un intento de login con su resultado ("success" | "failed").
            /// </summary>
            void RecordLogin(string outcome);

            /// <summary>
            /// Registra la creación de una compra con su resultado ("success" | "failed") y el monto (0 si falló).
            /// </summary>
            void RecordPurchase(string outcome, decimal amount);

            /// <summary>
            /// Registra una operación sobre stock requests ("create" | "approve" | "reject") y su resultado.
            /// </summary>
            void RecordStockRequest(string action, string outcome);

    }
}
