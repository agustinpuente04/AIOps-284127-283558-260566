namespace PharmaGo.ApiGateway.RateLimiting;

/// <summary>
/// Cubo de tokens (token bucket). El cubo se llena a una tasa constante hasta su capacidad y cada request
/// gasta un token. Frente a una ventana fija esto da tres propiedades que el límite anterior no tenía:
///  - tolera ráfagas legítimas hasta la capacidad;
///  - limita la tasa sostenida a la de recarga, sin el "doble ráfaga" de los bordes de ventana;
///  - se recupera de forma gradual: no existe un castigo de una hora que deja fuera a todos los que comparten IP.
/// El tiempo se recibe como parámetro (ms monotónicos) para que sea determinista y fácil de probar.
/// </summary>
public sealed class TokenBucket
{
    private readonly object _lock = new();
    private readonly double _capacity;
    private readonly double _refillPerMs;
    private double _tokens;
    private long _lastRefillMs;

    public TokenBucket(double capacity, double refillPerMinute, long nowMs)
    {
        _capacity = capacity;
        _refillPerMs = refillPerMinute / 60_000.0;
        _tokens = capacity;
        _lastRefillMs = nowMs;
    }

    /// <summary>Gasta <paramref name="cost"/> tokens si hay; si no, indica cuánto falta para poder hacerlo.</summary>
    public bool TryConsume(long nowMs, out TimeSpan retryAfter, double cost = 1)
    {
        lock (_lock)
        {
            Refill(nowMs);
            if (_tokens >= cost)
            {
                _tokens -= cost;
                retryAfter = TimeSpan.Zero;
                return true;
            }
            retryAfter = _refillPerMs <= 0
                ? TimeSpan.FromMinutes(1)
                : TimeSpan.FromMilliseconds(Math.Ceiling((cost - _tokens) / _refillPerMs));
            return false;
        }
    }

    /// <summary>Consulta sin gastar: ¿hay tokens suficientes ahora?</summary>
    public bool HasTokens(long nowMs, out TimeSpan retryAfter, double cost = 1)
    {
        lock (_lock)
        {
            Refill(nowMs);
            if (_tokens >= cost) { retryAfter = TimeSpan.Zero; return true; }
            retryAfter = _refillPerMs <= 0
                ? TimeSpan.FromMinutes(1)
                : TimeSpan.FromMilliseconds(Math.Ceiling((cost - _tokens) / _refillPerMs));
            return false;
        }
    }

    /// <summary>
    /// Un cubo lleno no guarda información: recrearlo da exactamente el mismo estado. Por eso se puede
    /// descartar de memoria sin alterar ningún límite (barrido sin pérdida).
    /// </summary>
    public bool IsFull(long nowMs)
    {
        lock (_lock)
        {
            Refill(nowMs);
            return _tokens >= _capacity;
        }
    }

    private void Refill(long nowMs)
    {
        var elapsed = nowMs - _lastRefillMs;
        if (elapsed <= 0) return;
        _tokens = Math.Min(_capacity, _tokens + elapsed * _refillPerMs);
        _lastRefillMs = nowMs;
    }
}
