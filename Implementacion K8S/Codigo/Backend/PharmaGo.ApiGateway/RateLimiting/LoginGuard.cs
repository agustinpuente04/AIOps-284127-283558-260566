using System.Collections.Concurrent;

namespace PharmaGo.ApiGateway.RateLimiting;

/// <summary>
/// Backoff exponencial por cuenta para el login. Protege la CUENTA, no la IP: el atacante se frena aunque rote de
/// IP, y los demás usuarios que comparten su IP no se ven afectados.
///
/// Se llevan dos contadores independientes por intento fallido:
///  - clave "usuario|ip": umbral bajo. Un atacante solo bloquea su propia combinación, no al dueño de la cuenta
///    que entra desde otra IP.
///  - clave "usuario": umbral alto. Frena el ataque distribuido (muchas IPs contra una cuenta). Para que esto no
///    permita dejar fuera a la víctima, un (usuario, IP) con un login correcto reciente queda exento de este
///    bloqueo global (el "dispositivo conocido").
///
/// Tras llegar al umbral, cada fallo adicional duplica el bloqueo (base, 2x, 4x...) hasta un máximo. Mientras una
/// clave está bloqueada los intentos se rechazan antes de llegar a users-service y NO suman: martillar durante el
/// bloqueo no lo prolonga. Los usuarios inexistentes se tratan igual que los existentes, para no crear un oráculo
/// que distinga cuentas reales.
/// El estado vive en memoria de cada réplica del gateway (con 2 réplicas el umbral efectivo puede llegar al doble).
/// </summary>
public sealed class LoginGuard
{
    private sealed class State
    {
        public int Failures;
        public long LastFailureMs;
        public long LockedUntilMs;
    }

    private readonly ConcurrentDictionary<string, State> _states = new();
    private readonly ConcurrentDictionary<string, long> _knownGood = new();
    private readonly LoginOptions _o;
    private readonly int _maxKeys;

    public LoginGuard(LoginOptions options, int maxKeys)
    {
        _o = options;
        _maxKeys = maxKeys;
    }

    public int Count => _states.Count + _knownGood.Count;

    public bool IsLocked(string key, long nowMs, out TimeSpan remaining)
    {
        if (_states.TryGetValue(key, out var s))
        {
            long until;
            lock (s) until = s.LockedUntilMs;
            if (until > nowMs)
            {
                remaining = TimeSpan.FromMilliseconds(until - nowMs);
                return true;
            }
        }
        remaining = TimeSpan.Zero;
        return false;
    }

    /// <summary>Registra un intento fallido; devuelve el bloqueo resultante (cero si aún no se alcanzó el umbral).</summary>
    public TimeSpan RecordFailure(string key, int threshold, long nowMs)
    {
        if (!_states.TryGetValue(key, out var s))
        {
            // Tope de memoria: pasado el límite las cuentas nuevas no se rastrean (el cubo por IP sigue activo).
            if (_states.Count >= _maxKeys) return TimeSpan.Zero;
            s = _states.GetOrAdd(key, _ => new State());
        }

        lock (s)
        {
            // La ventana cuenta desde que TERMINA el último bloqueo (o desde el último fallo si no hubo). Si contara solo
            // desde el último fallo, un bloqueo igual de largo que la ventana borraría el historial justo al vencer y el
            // atacante recuperaría los intentos iniciales en cada ciclo.
            if (s.Failures > 0 && nowMs - Math.Max(s.LastFailureMs, s.LockedUntilMs) > _o.FailureWindowSeconds * 1000L) s.Failures = 0;
            s.Failures++;
            s.LastFailureMs = nowMs;
            if (s.Failures < threshold) return TimeSpan.Zero;

            var exponent = Math.Min(s.Failures - threshold, 20);
            var seconds = Math.Min((double)_o.MaxLockSeconds, _o.BaseLockSeconds * Math.Pow(2, exponent));
            s.LockedUntilMs = nowMs + (long)(seconds * 1000);
            return TimeSpan.FromSeconds(seconds);
        }
    }

    /// <summary>Un login correcto limpia los fallos de ese (usuario, IP) y lo marca como conocido.</summary>
    public void RecordSuccess(string accountIpKey, long nowMs)
    {
        _states.TryRemove(accountIpKey, out _);
        if (_knownGood.Count < _maxKeys || _knownGood.ContainsKey(accountIpKey)) _knownGood[accountIpKey] = nowMs;
    }

    public bool IsKnownGood(string accountIpKey, long nowMs) =>
        _knownGood.TryGetValue(accountIpKey, out var at) && nowMs - at < _o.KnownGoodHours * 3_600_000L;

    /// <summary>Descarta estados que ya no influyen: sin bloqueo vigente y con la ventana de fallos vencida.</summary>
    public int Sweep(long nowMs)
    {
        var removed = 0;
        foreach (var pair in _states)
        {
            bool stale;
            lock (pair.Value)
                stale = pair.Value.LockedUntilMs <= nowMs && nowMs - Math.Max(pair.Value.LastFailureMs, pair.Value.LockedUntilMs) > _o.FailureWindowSeconds * 1000L;
            if (stale && _states.TryRemove(pair.Key, out _)) removed++;
        }
        foreach (var pair in _knownGood)
        {
            if (nowMs - pair.Value >= _o.KnownGoodHours * 3_600_000L && _knownGood.TryRemove(pair.Key, out _)) removed++;
        }
        return removed;
    }
}
