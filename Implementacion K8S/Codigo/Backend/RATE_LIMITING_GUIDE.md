# Rate limiting del API Gateway (token bucket)

El gateway limita las requests con **cubos de tokens separados por identidad** y protege el login con **backoff por cuenta**. Reemplaza al límite anterior (ventana fija por IP), que dejaba sin servicio a todos los usuarios que compartían una IP durante el resto de la hora y se podía eludir falsificando una cabecera.

Este documento explica **qué hace, cómo se configura y por qué se tomó cada decisión**. El código está en [`PharmaGo.ApiGateway/RateLimiting/`](PharmaGo.ApiGateway/RateLimiting/).

---

## 1. Qué problema había (medido en el cluster)

Los escenarios de caos ([`chaos-engineering/`](../chaos-engineering/README.md)) encontraron tres defectos del diseño anterior:

| Defecto | Cómo se midió |
|---|---|
| **Una IP bloqueaba a todos.** Tope duro de 1000 requests/hora por IP, y nginx hace que todos los usuarios entren por la misma IP. | R1 mandó ~50 000 requests desde una IP y el resto de ese origen quedó con 429 hasta que cambió la hora. |
| **El límite se eludía.** El gateway confiaba en la cabecera `X-Real-IP` que mandara el cliente. | Llegando directo al gateway con `X-Real-IP` rotativo pasaron **400 de 400** requests sin ningún 429. |
| **El modo `User` era peor.** Identificaba al usuario por los 8 primeros caracteres de `Authorization` sin validarlo, y confiaba en `X-Forwarded-For` y `X-User-Id` del cliente. | Revisión de código: un token inventado en cada request daba un cubo nuevo cada vez. |

## 2. Cómo funciona

```
cliente ─► nginx ─► [1 IP real] ─► [2 límite general] ─► [3 protección de login] ─► YARP ─► servicios
                     ClientIp       TokenBucketRateLimit    LoginProtection
```

1. **IP real** (`ClientIpMiddleware`): la IP del cliente se toma de `X-Forwarded-For` solo si quien se conectó es un pod de nginx.
2. **Límite general** (`TokenBucketRateLimitMiddleware`): cada request gasta un token del cubo que le corresponde:

| Quién es | Cubo | Capacidad / recarga |
|---|---|---|
| Sesión **válida** | `u:{userId}`, **independiente de su IP** | 200 / 300 por min |
| Sin sesión, o token falso/desconocido | `ip:{ip}` | 100 / 100 por min |
| Validar un token desconocido (consulta a la BD) | `lk:{ip}` | 30 / 30 por min |
| Intentos de login **fallidos** (dispositivo no conocido) | `lf:{ip}` | 10 / 10 por min |
| Requests de login (dispositivo no conocido) | `lg:{ip}` | 60 / 60 por min |

3. **Login** (`LoginProtectionMiddleware`), solo `POST /api/login`: backoff por cuenta y cubos propios por IP. **El login no gasta el cubo general** (ver D6).

Un 429 incluye `Retry-After` (segundos) y `X-RateLimit-Bucket` (`ip`, `user`, `account`, `login-ip` o `login-requests`).

---

## 3. Decisiones de diseño y por qué

### D1. Token bucket en lugar de ventana fija
Un token bucket tiene una capacidad (la ráfaga que tolera) y se recarga a ritmo constante (la tasa sostenida que permite).

- **Por qué:** la ventana fija tiene un precipicio: al agotar el cupo, todo recibe 429 hasta que cambia la ventana (una hora, en el caso del tope horario). El bucket se recupera **gradualmente**: pasados 2 segundos hay 2 tokens más. Medido: tras cesar una inundación, el tráfico anónimo vuelve a pasar en **1 segundo**.
- **Y tolera ráfagas legítimas** (una página que dispara 15 requests a la vez) sin permitir una tasa sostenida alta.
- **Descartado:** ventana deslizante (más cara en memoria y también castiga en bloque) y leaky bucket (no admite ráfagas).

### D2. Cubos separados por identidad: usuario autenticado vs IP
- **Por qué:** es lo que impide que una sola IP deje sin servicio a los demás. Un usuario con sesión válida tiene su cubo propio, que no se gasta con el tráfico de su IP.
- **Medido (escenario R4):** con la IP inundada por tráfico anónimo, el usuario autenticado de esa misma IP recibió **140 de 140 respuestas 200**, mientras el anónimo recibió 138 de 140 respuestas 429.
- **El cubo es por usuario, no por token:** un usuario con varias sesiones o dispositivos no multiplica su cuota.
- **Límite conocido:** todos los *anónimos* de una IP comparten un cubo (p. ej. una oficina entera sin sesión). El aislamiento es entre identidades y entre IPs.

### D3. El token se valida contra users-service; no se confía en el header
- **Por qué:** el aislamiento solo vale si la identidad está verificada. Si bastara con mandar un GUID, un atacante pondría uno distinto en cada request y obtendría un cubo nuevo cada vez (el defecto del modo `User` anterior).
- **Cómo:** el gateway no tiene BD, así que pregunta a `GET /internal/session/validate` de users-service (nuevo, `InternalSessionController`). Esa ruta **no está publicada** por el gateway (YARP no tiene ruta a `/internal`) ni por nginx (solo proxia `/api/`); es alcanzable solo desde dentro del cluster.
- **Caché de la validación:** 5 min si es válida, 30 s si no. Las sesiones no caducan ni hay logout, así que una validación positiva puede cachearse sin riesgo. La caché tiene tope de tamaño (50 000 tokens).
- **Presupuesto de consultas por IP (`lk:`):** cada validación cuesta una consulta a la BD; sin tope, mandar GUIDs falsos sería un ataque a la BD. Verificado en un test unitario: 200 tokens inventados desde una IP provocan ≤ 30 consultas a users-service.
- **Si users-service no responde:** el request se trata como anónimo (más estricto), nunca como autenticado.

### D4. Se eliminó el tope horario
- **Por qué:** era la causa directa del bloqueo de una hora. La tasa sostenida ya queda acotada por la recarga del bucket (100/min anónimo); un tope por hora solo añadía el castigo colateral.

### D5. La IP real solo se acepta si quien se conecta es nginx
`X-Real-IP` **se ignora por completo**. La IP viene de `X-Forwarded-For`, y solo si el peer es uno de los pods de nginx; se toma **únicamente la última entrada**, la que añade nginx con su `$remote_addr`. Lo que el cliente haya puesto antes en la cabecera se descarta.

- **Por qué no `ForwardedHeaders` con la red de pods (CIDR):** fue mi primera implementación y **no funcionó**: el puente CNI del nodo (`10.244.0.1`) cae dentro de `10.244.0.0/16`, así que cualquier tráfico originado en el nodo o en cualquier otro pod se consideraba "nginx" y su cabecera se obedecía. Se midió: seguían pasando 400 de 400. La opción CIDR sigue disponible (`TrustedProxyNetworks`) pero está documentada como la más amplia.
- **Cómo se confía solo en nginx:** un Service *headless* (`pharmago-ui-pods`, en `ui-service.yaml`) publica por DNS las IPs de los pods de nginx que están Ready. El gateway las resuelve cada 5 s (`TrustedProxyRefresher`) y reemplaza el conjunto de golpe. Medido: 204 de 400 requests con cabeceras rotativas (≈ la ráfaga de 2 réplicas), frente a 400 de 400 antes.
- **Trade-off:** CoreDNS cachea unos segundos. Un pod de nginx recién creado no es de confianza hasta que aparece en el DNS; mientras tanto sus usuarios cuentan bajo la IP de ese pod. Es **más estricto, nunca más laxo**, y solo dura segundos tras un rollout de la UI.
- **IPv6:** se agrupa por prefijo /64. Un cliente IPv6 suele tener un bloque entero; sin agrupar, rotar de dirección dentro de él devolvería la misma elusión.

### D6. Protección del login: backoff por cuenta
La fuerza bruta se frena por **cuenta**, no por IP, para que funcione aunque el atacante rote de IP y para no castigar a quien comparte su IP.

- **Dos contadores:** `(usuario, IP)` con umbral 5 y `usuario` global con umbral 20. El primero hace que un atacante solo bloquee *su propia* combinación y no al dueño que entra desde otra IP. El segundo frena el ataque distribuido (muchas IPs contra una cuenta).
- **El bloqueo global no deja fuera a la víctima:** un `(usuario, IP)` con un login correcto reciente (24 h) es un "dispositivo conocido" y queda exento del bloqueo global. Sin esto, un atacante podría bloquear a cualquiera apuntando a su cuenta.
- **Backoff exponencial:** el bloqueo dura 30 s y se duplica con cada fallo adicional hasta 15 min.
- **Los intentos durante un bloqueo se rechazan antes de llegar a users-service y no suman:** martillar durante el bloqueo no lo prolonga.
- **Los usuarios inexistentes se tratan igual que los reales:** si no, el bloqueo sería un oráculo para descubrir qué cuentas existen.
- **Qué cuenta como fallo:** 400, 401, 403 o 404 con un usuario legible en el body (users-service responde 404 si no existe y 400 si la clave es incorrecta). Los 5xx y 429 no cuentan.
- **Presupuesto de intentos fallidos por IP (`lf:`, 10 / 10 por min):** frena el *password spraying* (muchas cuentas desde una IP, ninguna llega al umbral). **Solo los fallos gastan**, para que una oficina entera entrando a las 9:00 no se bloquee a sí misma.
- **Tope de requests de login por IP (`lg:`, 60 / 60 por min):** el login no pasa por el cubo general, así que necesita su propio tope de tasa o se podría golpear sin límite (también se cobra aquí el body ilegible, para que no se pueda spamear basura).
- **El login NO gasta el cubo general `ip:`:** quien hace fuerza bruta a 300 req/min agotaría el cubo anónimo de la IP (100/min) y dejaría fuera a los usuarios legítimos de esa misma IP, que al hacer login no llevan token y solo tienen ese cubo. Separarlos tiene un costo aceptado: el login no cuenta para el límite de navegación.
- **Un dispositivo conocido queda exento de `lf:` y `lg:`:** el `(usuario, IP)` con un login correcto reciente sigue entrando aunque otro haga spraying desde su misma IP. Un usuario nuevo en esa IP sí recibe 429 con `Retry-After` de pocos segundos (no se le puede distinguir del atacante).
- **Tres bugs que encontraron las pruebas, no el diseño:**
  1. La ventana de fallos (15 min) se medía desde el último fallo, y el bloqueo máximo dura también 15 min: al vencer, el historial ya estaba borrado y el atacante recuperaba 4 intentos gratis por ciclo. Ahora la ventana cuenta desde que *termina* el bloqueo (test de regresión que falla con el bug original).
  2. El presupuesto de fallos por IP bloqueaba también a los usuarios legítimos de la misma IP durante un spraying (comprobado en el cluster: `admin` recibió 429). De ahí la exención del dispositivo conocido.
  3. Aun así, el dueño legítimo entraba solo 4 de 6 veces durante el ataque (escenario R2): el spraying agotaba el cubo *general* de la IP antes de llegar a la protección del login. De ahí que el login no gaste el cubo general.

### D7. Memoria acotada
- Un cubo lleno no guarda información (recrearlo da el mismo estado), así que cada 60 s se barren los cubos llenos **sin alterar ningún límite**.
- Tope de claves (100 000): pasado el tope, las claves nuevas comparten un cubo *overflow* estricto. Un atacante que invente IPs o usuarios no puede agotar la RAM del gateway.

### D8. Respuesta 429 informativa
`Retry-After` y `X-RateLimit-Bucket` permiten al cliente reintentar a tiempo y a un operador saber qué límite saltó. Se registran en nivel `Debug` y no en `Warning` para que una inundación no genere miles de líneas hacia Fluent-bit/Elasticsearch; las métricas HTTP por código de estado ya cuentan los 429.

### D9. Estado en memoria de cada réplica (sin Redis, por ahora)
- **Consecuencia medida:** con 2 réplicas el umbral efectivo de una cuenta puede llegar al doble (en la prueba manual el primer bloqueo apareció en el intento 10, no en el 5) y un reinicio del gateway borra el estado.
- **Por qué no Redis ahora:** añade un componente nuevo (otro punto de falla, con la decisión de si falla abierto o cerrado). Es el siguiente paso natural si se necesitan límites exactos; se dejó fuera para no ampliar el alcance.
- **Por qué no `sessionAffinity: ClientIP`:** el origen que ve kube-proxy es el pod de nginx, no el cliente; con 2 nginx habría 2 orígenes y se desbalancearía la carga entre réplicas.

### D10. Se elimina el modo `User`; el modo `IP` queda como retroceso
El modo `User` tenía los defectos de la sección 1 y nadie lo usaba (el valor por defecto era `IP`). `Mode: "IP"` conserva el comportamiento anterior (`AspNetCoreRateLimit`) solo como vía de retroceso, y `Mode: "Off"` lo desactiva.

### D11. Implementación propia en lugar de `AddRateLimiter`
El proyecto está en .NET 6 y el middleware `AddRateLimiter` es de .NET 7+. Se implementó el token bucket directamente (~70 líneas, con tiempo inyectable y sin dependencias), lo que además permite probarlo de forma determinista. Subir de versión sería un cambio mayor, ajeno a este alcance.

### D12. Exenciones
`/health`, `/metrics` y las peticiones `OPTIONS` (preflight CORS) no consumen tokens, igual que en la configuración anterior.

---

## 4. Parámetros

Todos viven en `appsettings.json` → `RateLimiting` y se pueden sobreescribir con variables de entorno (`RateLimiting__Anonymous__Capacity=50`).

| Parámetro | Valor | Por qué ese valor |
|---|---|---|
| `Anonymous` | 100 / 100 por min | Equivale al límite por minuto anterior, sin el tope horario. |
| `Authenticated` | 200 / 300 por min | Un usuario real dispara varias requests por pantalla; es más generoso porque su identidad está verificada. |
| `SessionLookups` | 30 / 30 por min | Alcanza de sobra (un usuario necesita 1 validación cada 5 min por réplica) y acota el costo de los tokens falsos. |
| `Login.FailedPerIp` | 10 / 10 por min | Permite algún error de tipeo, pero un spraying sostenido se frena. |
| `Login.RequestsPerIp` | 60 / 60 por min | Alcanza para una oficina que entra a primera hora (cada usuario nuevo gasta 1) y acota el golpeo del login. |
| `Login.AccountIpThreshold` | 5 | Errores humanos normales no llegan; un ataque sí. |
| `Login.AccountThreshold` | 20 | Alto a propósito: limita la capacidad de un atacante de dejar fuera a la víctima. |
| `Login.BaseLockSeconds` / `MaxLockSeconds` | 30 / 900 | Molesta poco al usuario real y escala rápido contra un ataque. |
| `Login.KnownGoodHours` | 24 | Un dispositivo conocido deja de serlo si no vuelve en un día. |
| `SessionValidation.PositiveCacheSeconds` | 300 | Las sesiones no caducan. |
| `TrustedProxyHosts` | `["pharmago-ui-pods"]` | Service headless de nginx. |
| `MaxTrackedKeys` | 100 000 | Tope de memoria: acota cuántas IPs/cuentas distintas se rastrean a la vez. |

## 5. Despliegue

1. Construir y cargar las imágenes de **users-service** y **api-gateway**. ⚠️ `minikube image load` **no reemplaza** una imagen con la misma etiqueta (quedan corriendo los pods con la imagen vieja, sin ningún aviso). Usar:
   ```bash
   docker save pharmago-api-gateway:latest | minikube image load -
   ```
   y comprobar el contenido: `kubectl exec <pod> -- grep -c TokenBucketRateLimitMiddleware /app/PharmaGo.ApiGateway.dll` (debe dar ≥ 1).
2. Aplicar `k8s/services/frontend/ui-service.yaml` (crea el Service headless `pharmago-ui-pods`) y `k8s/deployments/backend/api-gateway-deployment.yaml` (variable `RateLimiting__TrustedProxyHosts__0`).
3. `kubectl rollout restart` de **users-service primero** (para que exista el endpoint de validación) y después del gateway. Si el orden se invierte no hay caídas: mientras tanto los tokens se tratan como anónimos.

Sin `TrustedProxyHosts` ni `TrustedProxyNetworks` el gateway no confía en ninguna cabecera: todo el tráfico se cuenta bajo la IP del peer (nginx), es decir, seguro pero muy restrictivo.

## 6. Cómo probarlo

- **Tests unitarios** (`PharnaGo.Test/ApiGateway.Test`, 53 tests): token bucket, almacén de cubos, backoff del login, resolución de IP real y los dos middlewares, incluyendo aislamiento, tokens falsos, cabeceras falsificadas y spraying.
  ```bash
  dotnet test Backend/PharnaGo.Test/PharmaGo.Test.csproj --filter "FullyQualifiedName~ApiGateway.Test"
  ```
- **Escenarios de caos** (contra el cluster): `R1` ráfaga (con `--direct` intenta eludir el límite), `R2` fuerza bruta y spraying, `R4` aislamiento. Ver [`chaos-engineering/README.md`](../chaos-engineering/README.md).
- **Manual:** [`tutoriales/rate-limit/`](../tutoriales/rate-limit/README.md).

## 7. Limitaciones conocidas y siguientes pasos

| Limitación | Detalle | Posible mejora |
|---|---|---|
| Estado por réplica | Umbrales de login hasta ×2; un reinicio los borra (D9). | Contadores compartidos en Redis. |
| Usuario nuevo en una IP bajo spraying | Recibe 429 con `Retry-After` de pocos segundos; no se puede distinguir del atacante. | CAPTCHA o cookie de dispositivo. |
| Anónimos de una misma IP | Comparten un cubo para navegar (oficina sin sesión). El login queda aparte (D6). | Mantenerlo: es el límite razonable sin identidad. |
| Primera request tras el login | Si la IP agotó su presupuesto de validaciones, esa request cuenta como anónima hasta que la caché se llena. | Sembrar la caché con la respuesta del login. |
| Un pod de nginx comprometido | Sí puede falsificar `X-Forwarded-For`. | `NetworkPolicy` (requiere un CNI que las aplique, p. ej. Calico). |
| Tokens permanentes | Las sesiones no caducan ni hay logout, y el login reutiliza siempre el mismo token. | Fuera de alcance; relevante para seguridad. |
| Contraseñas en texto plano / 404 vs 400 | `LoginManager` compara la clave sin hash y distingue "usuario no existe" (404) de "clave incorrecta" (400): permite enumerar cuentas. | Fuera de alcance; no lo arregla este límite. |

## 8. Retroceso

```jsonc
"RateLimiting": { "Mode": "IP" }   // comportamiento anterior (AspNetCoreRateLimit, ventana fija por IP)
"RateLimiting": { "Mode": "Off" }  // sin límite
```
Por variable de entorno: `RateLimiting__Mode=IP`. Basta reiniciar los pods del gateway.
