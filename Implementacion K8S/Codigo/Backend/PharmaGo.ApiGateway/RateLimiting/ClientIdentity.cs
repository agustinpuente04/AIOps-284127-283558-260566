using Microsoft.AspNetCore.HttpOverrides;
using System.Net;
using System.Net.Sockets;

namespace PharmaGo.ApiGateway.RateLimiting;

/// <summary>
/// Clave de red del cliente. Se usa SIEMPRE <see cref="ConnectionInfo.RemoteIpAddress"/>: la cabecera X-Real-IP del
/// cliente se ignora por completo. Esa dirección ya es la real cuando la petición llega desde un proxy de confianza
/// (el middleware ForwardedHeaders la sustituye solo si el peer está en TrustedProxyNetworks); si no, es la del
/// socket y no se puede falsificar desde la petición.
/// </summary>
public static class ClientIdentity
{
    public static string IpKey(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
        if (ip == null) return "unknown";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        // En IPv6 cada cliente suele tener un /64 entero: agrupar por prefijo evita que rotar de dirección dentro de
        // su propio bloque devuelva el mismo bypass que X-Real-IP.
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = ip.GetAddressBytes();
            return "v6:" + Convert.ToHexString(bytes, 0, 8);
        }
        return ip.ToString();
    }

    /// <summary>El header Authorization de PharmaGo es el GUID de la sesión a secas (sin "Bearer").</summary>
    public static bool TryGetSessionToken(HttpContext context, out Guid token)
    {
        token = Guid.Empty;
        var header = context.Request.Headers.Authorization.ToString();
        return !string.IsNullOrEmpty(header) && Guid.TryParse(header.Trim(), out token) && token != Guid.Empty;
    }

    /// <summary>POST /api/login: tiene su propia protección (LoginProtectionMiddleware) y su propio cubo.</summary>
    public static bool IsLoginRequest(HttpContext context) =>
        HttpMethods.IsPost(context.Request.Method) && context.Request.Path.Equals("/api/login", StringComparison.OrdinalIgnoreCase);

    public static bool IsExempt(HttpContext context)
    {
        // Mismas exenciones que tenía la configuración anterior: salud, métricas y preflight CORS.
        if (HttpMethods.IsOptions(context.Request.Method)) return true;
        var path = context.Request.Path;
        return path.StartsWithSegments("/health") || path.StartsWithSegments("/metrics");
    }

    public static IPNetwork ParseNetwork(string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var prefix) || !int.TryParse(parts[1], out var length))
            throw new FormatException($"TrustedProxyNetworks: '{cidr}' no es un CIDR válido (ej. 10.244.0.0/16)");
        return new IPNetwork(prefix, length);
    }
}
