namespace PharmaGo.DataAccess.Resilience;

/// <summary>
/// Backoff exponencial con "equal jitter": la mitad de la espera es FIJA y la otra mitad es aleatoria.
///
///   techo  = min(tope, base · 2^(intento-1))
///   espera = techo/2 + aleatorio(0, techo/2)
///
/// Frente al jitter "completo" (aleatorio entre 0 y el techo) garantiza una espera mínima, así que un servicio que se
/// está reiniciando nunca recibe una ráfaga de reintentos inmediatos. Frente a ningún jitter (backoff puro) reparte los
/// reintentos en el tiempo, así que dos pods que fallaron al mismo instante no vuelven a golpear a la vez.
/// </summary>
public static class EqualJitter
{
    /// <param name="attempt">Número de reintento, empezando en 1.</param>
    /// <param name="random">Fuente de aleatoriedad en [0, 1); se inyecta para poder probarlo de forma determinista.</param>
    public static TimeSpan Delay(int attempt, TimeSpan baseDelay, TimeSpan maxDelay, Func<double> random)
    {
        if (attempt < 1) attempt = 1;
        // 2^30 ya supera cualquier tope razonable; evita el desborde del doble
        var exponent = Math.Min(attempt - 1, 30);
        var ceiling = Math.Min(maxDelay.TotalMilliseconds, baseDelay.TotalMilliseconds * Math.Pow(2, exponent));
        var half = ceiling / 2;
        return TimeSpan.FromMilliseconds(half + random() * half);
    }
}
