namespace PharmaGo.ApiGateway.RateLimiting;

public static class RateLimitingExtensions
{
    /// <summary>Registra el rate limiting por token bucket (cubos por identidad + protección de login).</summary>
    public static IServiceCollection AddTokenBucketRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("RateLimiting");
        var options = section.Get<RateLimitOptions>() ?? new RateLimitOptions();
        services.Configure<RateLimitOptions>(section);

        services.AddSingleton(new BucketStore(options.MaxTrackedKeys));
        services.AddSingleton(new LoginGuard(options.Login, options.MaxTrackedKeys));

        services.AddHttpClient(HttpSessionValidator.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(options.SessionValidation.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromMilliseconds(options.SessionValidation.TimeoutMs);
        });
        services.AddSingleton<ISessionValidator, HttpSessionValidator>();

        // La IP real (X-Forwarded-For) solo se lee si el peer es uno de los pods de nginx. Ver TrustedProxies.cs.
        services.AddSingleton(new TrustedProxies(options.TrustedProxyNetworks));
        services.AddHostedService<TrustedProxyRefresher>();
        return services;
    }

    /// <summary>IP real del cliente (solo si el peer es nginx). Debe ir antes que cualquier otro uso de la IP.</summary>
    public static IApplicationBuilder UseClientIp(this IApplicationBuilder app) => app.UseMiddleware<ClientIpMiddleware>();

    /// <summary>Orden: límite general por identidad -> protección de login.</summary>
    public static IApplicationBuilder UseTokenBucketRateLimiting(this IApplicationBuilder app) =>
        app.UseMiddleware<TokenBucketRateLimitMiddleware>()
           .UseMiddleware<LoginProtectionMiddleware>();
}
