namespace PharmaGo.Exceptions
{
    /// <summary>
    /// La base de datos no pudo atender la consulta a tiempo: timeout de comando, fallo transitorio de conexión, pool de
    /// conexiones agotado o circuit breaker abierto. Se traduce a HTTP 503 con Retry-After en los ExceptionFilter.
    /// No es un error de negocio ni de credenciales: por eso no cuenta como login/compra "fallido" en las métricas.
    /// </summary>
    public class DatabaseUnavailableException : Exception
    {
        /// <summary>Entidad cuyo acceso falló (p. ej. "Drug"). Cada entidad tiene su propio circuit breaker.</summary>
        public string Target { get; }

        /// <summary>Cuánto conviene esperar antes de reintentar (duración restante del breaker abierto).</summary>
        public TimeSpan RetryAfter { get; }

        public DatabaseUnavailableException(string message, string target, TimeSpan retryAfter, Exception? inner = null)
            : base(message, inner)
        {
            Target = target;
            RetryAfter = retryAfter;
        }
    }
}
