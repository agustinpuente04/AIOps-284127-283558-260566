using System.Collections.Concurrent;

namespace PharmaGo.ApiGateway.RateLimiting;

/// <summary>
/// Cubos por clave ("ip:1.2.3.4", "u:42", ...). Las claves llevan un prefijo que identifica el tipo de cubo.
/// Memoria acotada de dos formas:
///  1) barrido periódico de los cubos llenos (sin pérdida, ver <see cref="TokenBucket.IsFull"/>);
///  2) tope de claves: pasado el tope las claves nuevas comparten un único cubo "overflow" por tipo, que es
///     estricto a propósito. Un atacante que invente IPs o usuarios no puede agotar la RAM del gateway.
/// </summary>
public sealed class BucketStore
{
    private readonly ConcurrentDictionary<string, TokenBucket> _buckets = new();
    private readonly int _maxKeys;
    private long _lastSweepMs;

    public BucketStore(int maxKeys) => _maxKeys = maxKeys;

    public int Count => _buckets.Count;

    public TokenBucket GetOrCreate(string key, BucketOptions options, long nowMs)
    {
        if (_buckets.TryGetValue(key, out var existing)) return existing;
        if (_buckets.Count >= _maxKeys)
        {
            var prefixEnd = key.IndexOf(':');
            key = (prefixEnd >= 0 ? key[..(prefixEnd + 1)] : "") + "overflow";
        }
        return _buckets.GetOrAdd(key, _ => new TokenBucket(options.Capacity, options.RefillPerMinute, nowMs));
    }

    /// <summary>Elimina los cubos llenos. Devuelve cuántos quitó.</summary>
    public int Sweep(long nowMs)
    {
        var removed = 0;
        foreach (var pair in _buckets)
        {
            if (pair.Value.IsFull(nowMs) && _buckets.TryRemove(pair.Key, out _)) removed++;
        }
        return removed;
    }

    public void SweepIfDue(long nowMs, long everyMs = 60_000)
    {
        var last = Interlocked.Read(ref _lastSweepMs);
        if (nowMs - last < everyMs) return;
        if (Interlocked.CompareExchange(ref _lastSweepMs, nowMs, last) == last) Sweep(nowMs);
    }
}
