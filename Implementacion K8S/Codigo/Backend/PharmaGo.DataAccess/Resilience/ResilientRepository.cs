using System.Linq.Expressions;
using PharmaGo.Exceptions;
using PharmaGo.IDataAccess;
using Polly;
using Polly.CircuitBreaker;

namespace PharmaGo.DataAccess.Resilience;

/// <summary>
/// Decorador de <see cref="IRepository{T}"/> que aplica timeout-por-fallo, circuit breaker por entidad y retry con equal jitter
/// (ver <see cref="DbResiliencePolicies"/>). Es el único punto por el que pasa el acceso a datos de users-service y de
/// pharmacy-service, así que ningún manager ni filtro necesita saber que existe.
///
/// Los fallos de disponibilidad de la BD salen como <see cref="DatabaseUnavailableException"/> (HTTP 503); todo lo demás
/// (not found, validaciones, constraints) se propaga intacto.
/// </summary>
public class ResilientRepository<T> : IRepository<T> where T : class
{
    private readonly IRepository<T> _inner;
    private readonly DbResiliencePolicies _policies;
    private readonly string _target;

    public ResilientRepository(IRepository<T> inner, DbResiliencePolicies policies)
    {
        _inner = inner;
        _policies = policies;
        _target = "db:" + typeof(T).Name;
    }

    // ---- Lecturas: retry + breaker. Las consultas que devuelven colecciones se materializan DENTRO de la política:
    //      EF las devuelve diferidas y el SQL correría al serializar la respuesta, fuera de Polly y del ExceptionFilter.
    public IEnumerable<T> GetAllByExpression(Expression<Func<T, bool>> expression) =>
        Read(() => (IEnumerable<T>)_inner.GetAllByExpression(expression).ToList());

    public IEnumerable<T> GetAllBasicByExpression(Expression<Func<T, bool>> expression) =>
        Read(() => (IEnumerable<T>)_inner.GetAllBasicByExpression(expression).ToList());

    public T GetOneByExpression(Expression<Func<T, bool>> expression) => Read(() => _inner.GetOneByExpression(expression));

    public T GetOneDetailByExpression(Expression<Func<T, bool>> expression) => Read(() => _inner.GetOneDetailByExpression(expression));

    public bool Exists(Expression<Func<T, bool>> expression) => Read(() => _inner.Exists(expression));

    public bool Exists(T elem) => Read(() => _inner.Exists(elem));

    // ---- Insert/Update/Delete solo marcan cambios en el contexto: no hay viaje a la BD hasta Save()
    public void InsertOne(T elem) => _inner.InsertOne(elem);
    public void UpdateOne(T elem) => _inner.UpdateOne(elem);
    public void DeleteOne(T elem) => _inner.DeleteOne(elem);

    // ---- Escritura: breaker SIN retry. No es idempotente y un timeout no dice si el commit llegó a aplicarse.
    public void Save() => Run(() => { _policies.ForWrite(_target).Execute(_inner.Save); return 0; });

    private static bool LooksLikeSql(Exception exception)
    {
        for (var e = exception; e != null; e = e.InnerException)
            if (e.GetType().FullName?.StartsWith("Microsoft.Data.SqlClient", StringComparison.Ordinal) == true ||
                e.GetType().FullName?.StartsWith("System.Data.SqlClient", StringComparison.Ordinal) == true) return true;
        return false;
    }

    private TResult Read<TResult>(Func<TResult> query) => Run(() => _policies.ForRead(_target).Execute(query));

    private TResult Run<TResult>(Func<TResult> call)
    {
        try
        {
            return call();
        }
        catch (BrokenCircuitException ex)
        {
            _policies.Observer.OnFailure(_target, ex, handled: true, retryable: false);
            // Circuito abierto: no se llegó a tocar la BD. Se informa cuánto falta para que se pruebe de nuevo.
            throw new DatabaseUnavailableException(
                $"La base de datos no está disponible para '{typeof(T).Name}' (circuit breaker abierto).",
                _target, _policies.Options.BreakDuration, ex);
        }
        catch (Exception ex) when (_policies.Classifier.IsHandled(ex))
        {
            _policies.Observer.OnFailure(_target, ex, handled: true, retryable: _policies.Classifier.IsRetryable(ex));
            throw new DatabaseUnavailableException(
                $"La base de datos no respondió a tiempo para '{typeof(T).Name}'.",
                _target, _policies.Options.BreakDuration, ex);
        }
        catch (Exception ex) when (LooksLikeSql(ex))
        {
            // Un error de SQL que el clasificador NO reconoce: se registra para poder ampliar la lista, y se propaga intacto
            _policies.Observer.OnFailure(_target, ex, handled: false, retryable: false);
            throw;
        }
    }
}
