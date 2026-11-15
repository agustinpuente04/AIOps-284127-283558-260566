using System.Collections.Concurrent;
using Polly;
using Polly.CircuitBreaker;

namespace PharmaGo.DataAccess.Resilience;

/// <summary>
/// Políticas de Polly para el acceso a la BD, UNA POR ENTIDAD ("db:Drug", "db:Pharmacy"...). Es singleton porque el circuit
/// breaker es una instancia con estado que debe compartirse entre todas las requests y réplicas de repositorio del proceso.
///
/// Por qué un breaker por entidad y no uno para toda la BD: un bloqueo sobre la tabla Drugs abre solo el breaker de Drug,
/// de modo que las rutas que usan otras tablas siguen respondiendo (escenario C4). Un breaker global las arrastraría.
///
/// Composición (de fuera adentro):  lectura = retry( breaker( consulta ) )   escritura = breaker( consulta )
///  - cada intento cuenta para el breaker, así que un corte real lo abre rápido;
///  - el retry no reintenta una BrokenCircuitException (no es retryable): con el circuito abierto se falla en milisegundos;
///  - las escrituras (Save) no se reintentan: no son idempotentes y un timeout no dice si el commit llegó a aplicarse.
/// </summary>
public class DbResiliencePolicies
{
    private sealed record Entry(CircuitBreakerPolicy Breaker, ISyncPolicy Read, ISyncPolicy Write);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly DbResilienceOptions _options;
    private readonly IDbFailureClassifier _classifier;
    private readonly IDbResilienceObserver _observer;
    private readonly Func<double> _random;

    public DbResiliencePolicies(DbResilienceOptions options, IDbFailureClassifier classifier, IDbResilienceObserver observer, Func<double>? random = null)
    {
        _options = options;
        _classifier = classifier;
        _observer = observer;
        _random = random ?? (() => Random.Shared.NextDouble());
    }

    public DbResilienceOptions Options => _options;
    public IDbResilienceObserver Observer => _observer;
    public IDbFailureClassifier Classifier => _classifier;

    public ISyncPolicy ForRead(string target) => Get(target).Read;
    public ISyncPolicy ForWrite(string target) => Get(target).Write;
    public CircuitState StateOf(string target) => Get(target).Breaker.CircuitState;

    private Entry Get(string target) => _entries.GetOrAdd(target, Create);

    private Entry Create(string target)
    {
        var breaker = Policy
            .Handle<Exception>(_classifier.IsHandled)
            .CircuitBreaker(
                exceptionsAllowedBeforeBreaking: _options.BreakerFailures,
                durationOfBreak: _options.BreakDuration,
                onBreak: (_, _) => _observer.OnBreak(target),
                onReset: () => _observer.OnReset(target),
                onHalfOpen: () => _observer.OnHalfOpen(target));

        var retry = Policy
            .Handle<Exception>(_classifier.IsRetryable)
            .WaitAndRetry(
                retryCount: _options.RetryCount,
                sleepDurationProvider: attempt => EqualJitter.Delay(attempt, _options.RetryBaseDelay, _options.RetryMaxDelay, _random),
                onRetry: (_, _, _, _) => _observer.OnRetry(target));

        // Leer CircuitState evalúa también la transición por tiempo (abierto -> half-open), a diferencia de un valor guardado en un callback
        _observer.Register(target, () => breaker.CircuitState switch
        {
            CircuitState.Closed => 0.0,
            CircuitState.HalfOpen => 0.5,
            _ => 1.0
        });

        return new Entry(breaker, Policy.Wrap(retry, breaker), breaker);
    }
}
