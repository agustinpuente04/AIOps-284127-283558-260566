using Microsoft.Extensions.DependencyInjection;
using PharmaGo.IDataAccess;

namespace PharmaGo.DataAccess.Resilience;

public static class ServiceCollectionExtensions
{
    /// <summary>Registra las opciones y las políticas (singleton: el estado de cada breaker se comparte en todo el proceso).</summary>
    public static IServiceCollection AddDbResilience(
        this IServiceCollection services,
        DbResilienceOptions options,
        Func<IServiceProvider, IDbResilienceObserver>? observerFactory = null)
    {
        services.AddSingleton(options);
        services.AddSingleton<IDbFailureClassifier, SqlDbFailureClassifier>();
        services.AddSingleton<IDbResilienceObserver>(sp => observerFactory?.Invoke(sp) ?? new NullDbResilienceObserver());
        services.AddSingleton<DbResiliencePolicies>();
        return services;
    }

    /// <summary>
    /// Registra el repositorio concreto y expone <c>IRepository&lt;TEntity&gt;</c> envuelto en <see cref="ResilientRepository{T}"/>.
    /// Sustituye a <c>AddScoped&lt;IRepository&lt;TEntity&gt;, TRepository&gt;()</c>.
    /// </summary>
    public static IServiceCollection AddResilientRepository<TEntity, TRepository>(this IServiceCollection services)
        where TEntity : class
        where TRepository : class, IRepository<TEntity>
    {
        services.AddScoped<TRepository>();
        services.AddScoped<IRepository<TEntity>>(sp =>
            new ResilientRepository<TEntity>(sp.GetRequiredService<TRepository>(), sp.GetRequiredService<DbResiliencePolicies>()));
        return services;
    }
}
