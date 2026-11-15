namespace PharmaGo.ApiGateway.RateLimiting;

/// <summary>Parámetros de un token bucket: ráfaga máxima y tasa sostenida.</summary>
public class BucketOptions
{
    /// <summary>Tokens que caben en el cubo = ráfaga máxima que se tolera.</summary>
    public double Capacity { get; set; } = 100;

    /// <summary>Tokens que se recuperan por minuto = tasa sostenida permitida.</summary>
    public double RefillPerMinute { get; set; } = 100;
}

/// <summary>Protección del login: backoff por cuenta + presupuesto de intentos FALLIDOS por IP.</summary>
public class LoginOptions
{
    /// <summary>
    /// Intentos fallidos por IP. Solo se descuentan los fallos: los logins correctos no consumen,
    /// así una oficina entera entrando a las 9:00 no se bloquea a sí misma.
    /// </summary>
    public BucketOptions FailedPerIp { get; set; } = new() { Capacity = 10, RefillPerMinute = 10 };

    /// <summary>
    /// Requests de login por IP (éxitos incluidos), solo para dispositivos NO conocidos. Existe porque el login no gasta el
    /// cubo general: sin este tope se podría golpear el login sin límite de tasa. Los dispositivos conocidos no lo gastan.
    /// </summary>
    public BucketOptions RequestsPerIp { get; set; } = new() { Capacity = 60, RefillPerMinute = 60 };

    /// <summary>Fallos seguidos de un mismo (usuario, IP) antes de bloquear esa combinación.</summary>
    public int AccountIpThreshold { get; set; } = 5;

    /// <summary>
    /// Fallos de un usuario desde CUALQUIER IP antes de bloquearlo (frena el ataque distribuido).
    /// Más alto que el anterior para que un atacante no pueda dejar fuera a la víctima con facilidad.
    /// </summary>
    public int AccountThreshold { get; set; } = 20;

    /// <summary>Primer bloqueo; cada fallo adicional lo duplica hasta MaxLockSeconds.</summary>
    public int BaseLockSeconds { get; set; } = 30;
    public int MaxLockSeconds { get; set; } = 900;

    /// <summary>Si pasa este tiempo sin fallos, el contador de la cuenta vuelve a cero.</summary>
    public int FailureWindowSeconds { get; set; } = 900;

    /// <summary>Un (usuario, IP) con un login correcto reciente no sufre el bloqueo global de la cuenta.</summary>
    public int KnownGoodHours { get; set; } = 24;

    /// <summary>Tamaño máximo del body de login que se inspecciona para leer el usuario.</summary>
    public int MaxBodyBytes { get; set; } = 4096;
}

/// <summary>Cómo el gateway averigua si un token pertenece a una sesión válida.</summary>
public class SessionValidationOptions
{
    /// <summary>users-service: es quien tiene la tabla de sesiones.</summary>
    public string BaseUrl { get; set; } = "http://pharmago-users-service:80";
    public int TimeoutMs { get; set; } = 1500;

    /// <summary>
    /// Las sesiones no caducan ni hay logout, así que una validación correcta puede cachearse varios minutos.
    /// </summary>
    public int PositiveCacheSeconds { get; set; } = 300;
    public int NegativeCacheSeconds { get; set; } = 30;
    public int MaxCachedTokens { get; set; } = 50_000;
}

public class RateLimitOptions
{
    /// <summary>TokenBucket (por defecto), IP (modo anterior, AspNetCoreRateLimit) u Off.</summary>
    public string Mode { get; set; } = "TokenBucket";

    /// <summary>Cubo por IP para quien NO presenta una sesión válida.</summary>
    public BucketOptions Anonymous { get; set; } = new() { Capacity = 100, RefillPerMinute = 100 };

    /// <summary>Cubo por usuario autenticado: independiente del de su IP.</summary>
    public BucketOptions Authenticated { get; set; } = new() { Capacity = 200, RefillPerMinute = 300 };

    /// <summary>Presupuesto de validaciones de token por IP (cada una cuesta una consulta a la BD).</summary>
    public BucketOptions SessionLookups { get; set; } = new() { Capacity = 30, RefillPerMinute = 30 };

    public LoginOptions Login { get; set; } = new();
    public SessionValidationOptions SessionValidation { get; set; } = new();

    /// <summary>
    /// Nombres DNS que resuelven a los proxies de confianza: el Service headless que apunta a los pods de nginx.
    /// Es la opción recomendada porque confía SOLO en nginx. Si ambas listas están vacías no se confía en ninguna cabecera.
    /// </summary>
    public List<string> TrustedProxyHosts { get; set; } = new();
    public int TrustedProxyRefreshSeconds { get; set; } = 5;

    /// <summary>
    /// Redes (CIDR) de confianza. ATENCIÓN: usar la red de pods completa confía también en el puente del nodo y en
    /// cualquier otro pod, que podrían falsificar X-Forwarded-For. Solo para entornos sin DNS headless.
    /// </summary>
    public List<string> TrustedProxyNetworks { get; set; } = new();

    /// <summary>Tope de claves en memoria: evita que un atacante agote la RAM inventando IPs o usuarios.</summary>
    public int MaxTrackedKeys { get; set; } = 100_000;
}
