using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace PharmaGo.ApiGateway.RateLimiting;

public sealed record SessionInfo(int UserId, string Role);

/// <summary>
/// Decide si un token es una sesión real. Sin esto un atacante podría mandar un GUID distinto en cada request y
/// obtener un cubo nuevo cada vez: el cubo "por usuario" solo aísla si la identidad está verificada.
/// </summary>
public interface ISessionValidator
{
    /// <summary>Consulta solo la caché local (no cuesta nada). Devuelve true si ya se había validado ese token.</summary>
    bool TryGetCached(Guid token, out SessionInfo? session);

    /// <summary>Pregunta a users-service. Devuelve null si el token no es válido o el servicio no responde.</summary>
    Task<SessionInfo?> ValidateAsync(Guid token, CancellationToken cancellationToken);
}

/// <summary>
/// Valida contra GET /internal/session/validate de users-service (esa ruta no está publicada por el gateway).
/// Caché propia con tamaño acotado: positivas varios minutos (las sesiones no caducan), negativas pocos segundos.
/// Si users-service no responde no se cachea nada y el request se trata como anónimo (más estricto, nunca más laxo).
/// </summary>
public sealed class HttpSessionValidator : ISessionValidator, IDisposable
{
    private sealed record Entry(SessionInfo? Session);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SessionValidationOptions _options;
    private readonly MemoryCache _cache;

    public const string HttpClientName = "session-validator";

    public HttpSessionValidator(IHttpClientFactory httpClientFactory, IOptions<RateLimitOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value.SessionValidation;
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = _options.MaxCachedTokens });
    }

    public bool TryGetCached(Guid token, out SessionInfo? session)
    {
        if (_cache.TryGetValue(token, out Entry? entry) && entry != null)
        {
            session = entry.Session;
            return true;
        }
        session = null;
        return false;
    }

    public async Task<SessionInfo?> ValidateAsync(Guid token, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, "internal/session/validate");
            request.Headers.TryAddWithoutValidation("Authorization", token.ToString());
            using var response = await client.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var json = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
                var root = json.RootElement;
                var session = new SessionInfo(root.GetProperty("userId").GetInt32(), root.GetProperty("role").GetString() ?? "");
                Store(token, session, _options.PositiveCacheSeconds);
                return session;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                Store(token, null, _options.NegativeCacheSeconds);
                return null;
            }
            return null;   // 5xx u otro: no se sabe, no se cachea
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;   // timeout o users-service caído: tratar como anónimo
        }
    }

    private void Store(Guid token, SessionInfo? session, int seconds) =>
        _cache.Set(token, new Entry(session), new MemoryCacheEntryOptions
        {
            Size = 1,
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(seconds)
        });

    public void Dispose() => _cache.Dispose();
}
