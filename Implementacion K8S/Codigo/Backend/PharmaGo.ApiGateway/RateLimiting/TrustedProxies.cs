using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace PharmaGo.ApiGateway.RateLimiting;

/// <summary>
/// Conjunto de proxies en los que el gateway confía para leer la IP real del cliente en X-Forwarded-For.
/// Se compone de IPs exactas (las de los pods de nginx, resueltas por DNS y refrescadas en segundo plano) y,
/// opcionalmente, de redes CIDR. Confiar en toda la red de pods NO es equivalente a "solo nginx": el puente del nodo y
/// cualquier otro pod caen dentro, y podrían falsificar la cabecera. Por eso el valor por defecto son solo IPs exactas.
/// </summary>
public sealed class TrustedProxies
{
    private volatile HashSet<IPAddress> _addresses = new();
    private readonly List<IPNetwork> _networks;

    public TrustedProxies(IEnumerable<string> cidrs) =>
        _networks = cidrs.Select(ClientIdentity.ParseNetwork).ToList();

    public int Count => _addresses.Count;

    /// <summary>Reemplaza el conjunto de IPs exactas de golpe (el HashSet anterior nunca se modifica: es seguro entre hilos).</summary>
    public void SetAddresses(IEnumerable<IPAddress> addresses) =>
        _addresses = new HashSet<IPAddress>(addresses.Select(Normalize));

    public bool IsTrusted(IPAddress? peer)
    {
        if (peer == null) return false;
        peer = Normalize(peer);
        if (_addresses.Contains(peer)) return true;
        foreach (var network in _networks) if (network.Contains(peer)) return true;
        return false;
    }

    private static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
}

/// <summary>
/// Resuelve cada cierto tiempo los nombres configurados (un Service headless que apunta a los pods de nginx) y
/// actualiza <see cref="TrustedProxies"/>. Si el DNS falla se conserva el último conjunto conocido.
/// Mientras un pod de nginx recién creado no aparece en el DNS (CoreDNS cachea unos segundos) sus requests se
/// consideran no confiables: se cuentan por la IP de ese pod, es decir, el límite es más estricto, nunca más laxo.
/// </summary>
public sealed class TrustedProxyRefresher : BackgroundService
{
    private readonly TrustedProxies _trusted;
    private readonly RateLimitOptions _options;
    private readonly ILogger<TrustedProxyRefresher> _logger;
    private string _last = "";

    public TrustedProxyRefresher(TrustedProxies trusted, IOptions<RateLimitOptions> options, ILogger<TrustedProxyRefresher> logger)
    {
        _trusted = trusted;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.TrustedProxyHosts.Count == 0) return;
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.TrustedProxyRefreshSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            await RefreshAsync(stoppingToken);
            try { await Task.Delay(interval, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    internal async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var resolved = new List<IPAddress>();
        foreach (var host in _options.TrustedProxyHosts)
        {
            try { resolved.AddRange(await Dns.GetHostAddressesAsync(host, cancellationToken)); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("No se pudo resolver el proxy de confianza '{Host}': {Error}. Se conserva el conjunto anterior.", host, ex.Message);
                return;
            }
        }
        if (resolved.Count == 0) return;   // un Service sin pods Ready no debe vaciar la lista de golpe

        _trusted.SetAddresses(resolved);
        var snapshot = string.Join(",", resolved.Select(a => a.ToString()).OrderBy(a => a));
        if (snapshot != _last)
        {
            _last = snapshot;
            _logger.LogInformation("Proxies de confianza (nginx): {Proxies}", snapshot);
        }
    }
}

/// <summary>
/// Sustituye la IP del socket por la del cliente real SOLO si el peer es un proxy de confianza. Toma únicamente la
/// ÚLTIMA entrada de X-Forwarded-For, que es la que añadió ese proxy con su $remote_addr; todo lo que el cliente haya
/// puesto antes en la cabecera se ignora. Si el peer no es de confianza la cabecera no se mira: no se puede falsificar.
/// La cabecera X-Real-IP no se usa para nada.
/// </summary>
public sealed class ClientIpMiddleware
{
    private readonly RequestDelegate _next;
    private readonly TrustedProxies _trusted;

    public ClientIpMiddleware(RequestDelegate next, TrustedProxies trusted)
    {
        _next = next;
        _trusted = trusted;
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (_trusted.IsTrusted(context.Connection.RemoteIpAddress) &&
            context.Request.Headers.TryGetValue("X-Forwarded-For", out var forwarded))
        {
            var last = forwarded.ToString().Split(',')[^1].Trim();
            if (IPAddress.TryParse(last, out var client)) context.Connection.RemoteIpAddress = client;
        }
        return _next(context);
    }
}
