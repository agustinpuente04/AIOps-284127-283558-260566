using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace PharmaGo.ApiGateway.RateLimiting;

/// <summary>
/// Protección de POST /api/login contra fuerza bruta y password spraying. Complementa al límite general:
///  1. Backoff por cuenta (<see cref="LoginGuard"/>): frena al atacante aunque rote de IP.
///  2. Presupuesto de intentos FALLIDOS por IP: frena el spraying (muchas cuentas desde una IP). Los logins
///     correctos no gastan nada, de modo que una oficina o NAT con muchos usuarios legítimos no se bloquea.
///
/// El usuario se lee del body JSON (con tope de tamaño) y el body se rebobina para que YARP lo reenvíe intacto.
/// El resultado se conoce después del proxy: 2xx = éxito; 400/401/403/404 con usuario presente = fallo de
/// credenciales (users-service responde 404 si el usuario no existe y 400 si la clave es incorrecta).
/// </summary>
public sealed class LoginProtectionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly BucketStore _store;
    private readonly LoginGuard _guard;
    private readonly LoginOptions _options;
    private long _lastSweepMs;

    public LoginProtectionMiddleware(RequestDelegate next, BucketStore store, LoginGuard guard, IOptions<RateLimitOptions> options)
    {
        _next = next;
        _store = store;
        _guard = guard;
        _options = options.Value.Login;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method) || !context.Request.Path.Equals("/api/login", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var now = Environment.TickCount64;
        SweepIfDue(now);
        var ip = ClientIdentity.IpKey(context);
        var userName = await ReadUserNameAsync(context);

        var requestsPerIp = _store.GetOrCreate("lg:" + ip, _options.RequestsPerIp, now);

        if (userName == null)
        {
            // Sin usuario legible (body inválido, vacío o enorme): users-service lo rechazará, pero como el login no pasa por
            // el cubo general se cobra aquí, o se podría spamear basura sin límite.
            if (!requestsPerIp.TryConsume(now, out var garbageRetry))
            {
                await RateLimitResponse.WriteAsync(context, "login-requests", garbageRetry, "Too many login requests from this address");
                return;
            }
            await _next(context);
            return;
        }

        var accountIp = userName + "|" + ip;
        var knownDevice = _guard.IsKnownGood(accountIp, now);
        var failedPerIp = _store.GetOrCreate("lf:" + ip, _options.FailedPerIp, now);

        // 1) bloqueos vigentes. El de la cuenta global no aplica a un (usuario, IP) con un login correcto reciente.
        if (_guard.IsLocked(accountIp, now, out var remaining) ||
            (!_guard.IsKnownGood(accountIp, now) && _guard.IsLocked(userName, now, out remaining)))
        {
            await RateLimitResponse.WriteAsync(context, "account", remaining, "Too many failed login attempts for this account");
            return;
        }

        // 2) presupuesto de fallos de esta IP (sin gastarlo: solo se gasta si el intento falla). Un (usuario, IP) con un
        //    login correcto reciente queda exento: si no, un atacante que hace spraying desde una IP compartida dejaría
        //    sin poder entrar ni a los usuarios legítimos de esa misma IP.
        if (!knownDevice && !failedPerIp.HasTokens(now, out var retryAfter))
        {
            await RateLimitResponse.WriteAsync(context, "login-ip", retryAfter, "Too many failed login attempts from this address");
            return;
        }

        // 3) tope de requests de login de esta IP. Un dispositivo conocido no lo gasta: sigue entrando aunque otro
        //    esté haciendo spraying desde su misma IP.
        if (!knownDevice && !requestsPerIp.TryConsume(now, out var requestsRetry))
        {
            await RateLimitResponse.WriteAsync(context, "login-requests", requestsRetry, "Too many login requests from this address");
            return;
        }

        await _next(context);

        var status = context.Response.StatusCode;
        var end = Environment.TickCount64;
        if (status >= 200 && status < 300)
        {
            _guard.RecordSuccess(accountIp, end);
        }
        else if (status is 400 or 401 or 403 or 404)
        {
            failedPerIp.TryConsume(end, out _);
            _guard.RecordFailure(accountIp, _options.AccountIpThreshold, end);
            _guard.RecordFailure(userName, _options.AccountThreshold, end);
        }
        // 5xx / 429: no es un fallo de credenciales, no se cuenta.
    }

    private void SweepIfDue(long nowMs)
    {
        var last = Interlocked.Read(ref _lastSweepMs);
        if (nowMs - last >= 60_000 && Interlocked.CompareExchange(ref _lastSweepMs, nowMs, last) == last) _guard.Sweep(nowMs);
    }

    /// <summary>Lee "userName" del body sin consumirlo. Devuelve null si no hay un usuario utilizable.</summary>
    private async Task<string?> ReadUserNameAsync(HttpContext context)
    {
        var max = _options.MaxBodyBytes;
        if (context.Request.ContentLength is long declared && declared > max) return null;

        context.Request.EnableBuffering();
        var buffer = new byte[max + 1];
        var read = 0;
        int n;
        while (read < buffer.Length && (n = await context.Request.Body.ReadAsync(buffer.AsMemory(read))) > 0) read += n;
        context.Request.Body.Position = 0;
        if (read == 0 || read > max) return null;

        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, read));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (!property.NameEquals("userName") && !property.Name.Equals("userName", StringComparison.OrdinalIgnoreCase)) continue;
                if (property.Value.ValueKind != JsonValueKind.String) return null;
                var name = property.Value.GetString()?.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(name)) return null;
                return name.Length > 64 ? name[..64] : name;
            }
        }
        catch (JsonException) { }
        return null;
    }
}
