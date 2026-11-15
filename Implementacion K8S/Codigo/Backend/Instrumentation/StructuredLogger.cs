using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using InstrumentationInterface;

namespace Instrumentation
{
    public class StructuredLogger : IStructuredLogger
    {
        private readonly ILogger _logger;
        private readonly IHostEnvironment _hostEnvironment;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public StructuredLogger(
            ILogger<StructuredLogger> logger,
            IHostEnvironment hostEnvironment,
            IHttpContextAccessor httpContextAccessor)
        {
            _logger = logger;
            _hostEnvironment = hostEnvironment;
            _httpContextAccessor = httpContextAccessor;
        }

        public void LogInformation(string message, Dictionary<string, object>? metadata = null)
        {
            var correlationId = ResolveCorrelationId();
            var fullMessage = AppendPharmaBizSuffix(message, metadata);
            var scopeData = BuildScopeData("Information", metadata, correlationId);
            using (_logger.BeginScope(scopeData))
            {
                _logger.LogInformation("{StructuredMessage} correlation_id={correlation_id}", fullMessage, correlationId);
            }
        }

        public void LogWarning(string message, Exception? exception = null, Dictionary<string, object>? metadata = null)
        {
            var correlationId = ResolveCorrelationId();
            var fullMessage = AppendPharmaBizSuffix(message, metadata);
            var scopeData = BuildScopeData("Warning", metadata, correlationId);
            using (_logger.BeginScope(scopeData))
            {
                if (exception != null)
                {
                    _logger.LogWarning(exception, "{StructuredMessage} correlation_id={correlation_id}", fullMessage, correlationId);
                }
                else
                {
                    _logger.LogWarning("{StructuredMessage} correlation_id={correlation_id}", fullMessage, correlationId);
                }
            }
        }

        public void LogError(string message, Exception? exception = null, Dictionary<string, object>? metadata = null)
        {
            var correlationId = ResolveCorrelationId();
            var fullMessage = AppendPharmaBizSuffix(message, metadata);
            var scopeData = BuildScopeData("Error", metadata, correlationId);
            using (_logger.BeginScope(scopeData))
            {
                if (exception != null)
                {
                    _logger.LogError(exception, "{StructuredMessage} correlation_id={correlation_id}", fullMessage, correlationId);
                }
                else
                {
                    _logger.LogError("{StructuredMessage} correlation_id={correlation_id}", fullMessage, correlationId);
                }
            }
        }

        /// <summary>
        /// Marca eventos de negocio para Kibana: <c>pharma_biz=...</c> (legible) + token <c>EVT...</c> (una sola palabra,
        /// sin = ni _, para que Lucene encuentre con <c>log:EVTPUCR</c> sin comodines prohibidos en campos analizados).
        /// </summary>
        private static string AppendPharmaBizSuffix(string message, Dictionary<string, object>? metadata)
        {
            if (metadata == null || !metadata.TryGetValue("pharma_biz", out var v) || v == null)
                return message;
            var biz = Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
            var kibanaToken = PharmaBizToKibanaToken(biz);
            if (string.IsNullOrEmpty(kibanaToken))
                return $"{message} pharma_biz={biz}";
            return $"{message} pharma_biz={biz} {kibanaToken}";
        }

        /// <summary>Tokens únicos para búsqueda Lucene tipo <c>log:EVTPUCR</c> (el analizador suele pasarlos a minúsculas).</summary>
        private static string? PharmaBizToKibanaToken(string pharmaBiz) => pharmaBiz switch
        {
            "login_success" => "EVTLGOK",
            "login_fail" => "EVTLGFL",
            "purchase_create" => "EVTPUCR",
            "purchase_create_fail" => "EVTPUCF",
            _ => null
        };

        private string ResolveCorrelationId()
        {
            var ctx = _httpContextAccessor.HttpContext;
            if (ctx == null)
                return "none";

            if (ctx.Items.TryGetValue(CorrelationIdMiddlewareExtensions.HttpContextItemKey, out var item)
                && item is string fromItems
                && !string.IsNullOrEmpty(fromItems))
                return fromItems;

            var header = ctx.Request.Headers["X-Correlation-ID"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(header))
                return header.Trim();

            return ctx.TraceIdentifier;
        }

        private Dictionary<string, object> BuildScopeData(string logLevel, Dictionary<string, object>? metadata, string correlationId)
        {
            var scopeData = new Dictionary<string, object>
            {
                ["timestamp_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["level"] = ToLevelLabel(logLevel),
                ["service"] = _hostEnvironment.ApplicationName,
                ["correlation_id"] = correlationId
            };

            if (metadata != null)
            {
                foreach (var kvp in metadata)
                {
                    scopeData[kvp.Key] = kvp.Value;
                }
            }

            return scopeData;
        }

        private static string ToLevelLabel(string logLevel) => logLevel switch
        {
            "Information" => "info",
            "Warning" => "warn",
            "Error" => "error",
            _ => logLevel.ToLowerInvariant()
        };
    }
}
