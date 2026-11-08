# Cómo ajustar el Rate Limiting

Todo está en `Backend/PharmaGo.ApiGateway/appsettings.json`, sección `RateLimiting`. Cada valor se puede sobreescribir por variable de entorno con `__` como separador (`RateLimiting__Anonymous__Capacity=50`), sin recompilar. Tras cambiarlo basta reiniciar los pods del gateway.
Qué hace cada parámetro y por qué tiene su valor actual: [`Backend/RATE_LIMITING_GUIDE.md`](../../Backend/RATE_LIMITING_GUIDE.md).

## Modo TokenBucket (por defecto)

Cada cubo se define con dos números: **`Capacity`** (la ráfaga que tolera) y **`RefillPerMinute`** (la tasa sostenida que permite).

```json
{
  "RateLimiting": {
    "Mode": "TokenBucket",
    "Anonymous":     { "Capacity": 100, "RefillPerMinute": 100 },   // quien no tiene sesión válida, por IP
    "Authenticated": { "Capacity": 200, "RefillPerMinute": 300 },   // usuario con sesión válida, por usuario
    "SessionLookups":{ "Capacity": 30,  "RefillPerMinute": 30 },    // validaciones de token por IP
    "Login": {
      "FailedPerIp": { "Capacity": 10, "RefillPerMinute": 10 },     // intentos de login FALLIDOS por IP
      "RequestsPerIp": { "Capacity": 60, "RefillPerMinute": 60 },   // requests de login por IP (dispositivos no conocidos)
      "AccountIpThreshold": 5,                                      // fallos de (usuario, IP) antes de bloquear
      "AccountThreshold": 20,                                       // fallos de un usuario desde cualquier IP
      "BaseLockSeconds": 30,                                        // primer bloqueo; se duplica en cada fallo
      "MaxLockSeconds": 900
    }
  }
}
```

Reglas para elegir valores:

- **Más ráfaga, misma tasa:** sube `Capacity`. **Menos tasa sostenida:** baja `RefillPerMinute`.
- `Authenticated` debe ser **mayor** que `Anonymous`: la identidad está verificada.
- Con N réplicas del gateway el límite efectivo es hasta N veces lo configurado (cada réplica tiene sus cubos).
- Bajar `AccountIpThreshold` endurece el login pero aumenta el riesgo de molestar a quien se equivoca al tipear.

## Quién es de confianza para la IP real

```yaml
# k8s/deployments/backend/api-gateway-deployment.yaml
- name: RateLimiting__TrustedProxyHosts__0
  value: "pharmago-ui-pods"      # Service headless: las IPs de los pods de nginx
```

La IP del cliente se lee de `X-Forwarded-For` **solo** si quien se conecta es uno de esos pods. Sin esto, todo el tráfico cuenta como una sola IP (la de nginx). No uses la red de pods completa como `TrustedProxyNetworks`: incluye el puente del nodo y cualquier otro pod, que podrían falsificar la cabecera.

## Endpoints específicos

El token bucket aplica a todas las rutas por igual (excepto `/health`, `/metrics` y `OPTIONS`). El login **no** gasta el cubo general: tiene su propia protección (backoff por cuenta + cubos de fallos y de requests por IP), configurada en `Login`.

## Modo IP (retroceso al límite anterior)

```json
{
  "RateLimiting": { "Mode": "IP" },
  "IpRateLimiting": {
    "GeneralRules": [
      { "Endpoint": "*", "Period": "1m", "Limit": 100 },
      { "Endpoint": "*", "Period": "1h", "Limit": 1000 }
    ]
  }
}
```

Es la ventana fija por IP con `AspNetCoreRateLimit`. Se conserva solo como vía de retroceso: tiene el bloqueo de una hora y confía en `X-Real-IP`.

## Deshabilitar

```json
{ "RateLimiting": { "Mode": "Off" } }
```
