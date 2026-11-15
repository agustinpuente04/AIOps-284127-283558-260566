using Microsoft.Extensions.Options;

namespace PharmaGo.ApiGateway.RateLimiting;

/// <summary>
/// Límite general de requests con cubos separados por identidad:
///  - sesión válida  -> cubo del USUARIO ("u:{id}"), independiente de su IP;
///  - cualquier otro -> cubo de la IP ("ip:{ip}"), más chico.
/// Así una IP que inunda con tráfico anónimo (o con tokens inventados) agota solo su propio cubo anónimo y no el de
/// los usuarios autenticados, aunque compartan IP.
///
/// Un token desconocido obliga a preguntar a users-service (una consulta a la BD). Para que eso no sea una vía de
/// ataque, esas consultas salen de un presupuesto por IP ("lk:{ip}"); si se agota, el request sigue como anónimo.
/// Los 429 llevan Retry-After: el cliente sabe cuándo reintentar y el cubo se recupera en segundos, no en una hora.
/// </summary>
public sealed class TokenBucketRateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly BucketStore _store;
    private readonly ISessionValidator _validator;
    private readonly RateLimitOptions _options;
    private readonly ILogger<TokenBucketRateLimitMiddleware> _logger;

    public TokenBucketRateLimitMiddleware(
        RequestDelegate next,
        BucketStore store,
        ISessionValidator validator,
        IOptions<RateLimitOptions> options,
        ILogger<TokenBucketRateLimitMiddleware> logger)
    {
        _next = next;
        _store = store;
        _validator = validator;
        _options = options.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // El login no gasta el cubo general: lo gestiona LoginProtectionMiddleware con cubos propios. Si lo gastara, quien
        // hace fuerza bruta desde una IP agotaría el cubo anónimo y dejaría sin poder entrar a los usuarios legítimos de
        // esa misma IP (medido en el escenario R2: el dueño entró solo 4 de 6 veces durante el ataque).
        if (ClientIdentity.IsExempt(context) || ClientIdentity.IsLoginRequest(context))
        {
            await _next(context);
            return;
        }

        var now = Environment.TickCount64;
        _store.SweepIfDue(now);
        var ip = ClientIdentity.IpKey(context);

        SessionInfo? session = null;
        if (ClientIdentity.TryGetSessionToken(context, out var token) && !_validator.TryGetCached(token, out session))
        {
            var lookups = _store.GetOrCreate("lk:" + ip, _options.SessionLookups, now);
            if (lookups.TryConsume(now, out _))
                session = await _validator.ValidateAsync(token, context.RequestAborted);
        }

        TokenBucket bucket;
        string label;
        if (session != null)
        {
            bucket = _store.GetOrCreate("u:" + session.UserId, _options.Authenticated, now);
            label = "user";
        }
        else
        {
            bucket = _store.GetOrCreate("ip:" + ip, _options.Anonymous, now);
            label = "ip";
        }

        if (!bucket.TryConsume(now, out var retryAfter))
        {
            _logger.LogDebug("Rate limit ({Bucket}) excedido para {Key}", label, session != null ? "user:" + session.UserId : ip);
            await RateLimitResponse.WriteAsync(context, label, retryAfter, "Too many requests");
            return;
        }

        await _next(context);
    }
}

internal static class RateLimitResponse
{
    public static async Task WriteAsync(HttpContext context, string bucket, TimeSpan retryAfter, string message)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers["Retry-After"] = seconds.ToString();
        context.Response.Headers["X-RateLimit-Bucket"] = bucket;
        await context.Response.WriteAsJsonAsync(new { error = message, bucket, retryAfterSeconds = seconds });
    }
}
