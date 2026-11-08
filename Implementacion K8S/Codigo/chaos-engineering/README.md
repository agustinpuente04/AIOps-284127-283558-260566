# Chaos Engineering - PharmaGo

Suite de 21 experimentos de caos sobre la aplicación desplegada en Kubernetes. Cubre los seis tipos de fallo del requerimiento:

| Tipo de fallo | Escenarios |
|---|---|
| Inyección de requests | R1, R2, R3, R4 |
| Sobrecarga de CPU | C1, C2, C3, C4 (BD saturada: bloqueos + consultas pesadas) |
| Sobrecarga de memoria | M1, M2 |
| Sobrecarga de storage | S1, S2, S3 |
| Interrupción de tráfico de red | N1, N2, N3 |
| Desconexión de componentes internos | D1, D2, D3, D4 |
| Combinado | G1 |

Cada escenario es un **experimento con hipótesis**, no solo una inyección: verifica el estado estable, mide la disponibilidad real mientras inyecta el fallo, revierte todo al terminar (aunque se corte con Ctrl+C) y emite un veredicto PASS/FAIL con un informe en `reports/`.

## Requisitos

- Cluster Minikube con la app desplegada (`k8s/apply-k8s.sh`) y todos los pods `Ready`.
- `kubectl` >= 1.27 (usa `kubectl debug --profile=netadmin`), `curl`, `awk`. Bash >= 4: Git Bash en Windows, Linux o macOS con bash de Homebrew.
- Para los escenarios de red (N1-N3, G1): el nodo debe poder descargar `nicolaka/netshoot` de Docker Hub. Se descarga una sola vez y puede tardar unos minutos.
- No hay que preparar nada más: la URL de la UI se obtiene sola (`minikube ip` + NodePort; si no es accesible, como con el driver Docker en Windows, abre un túnel `minikube service` en segundo plano y lo cierra al terminar). Se puede forzar con `--url` o `PHARMAGO_URL`.

## Uso

```bash
cd "Implementacion K8S/Codigo/chaos-engineering"

./chaos.sh list                          # escenarios, categorías y duraciones
./chaos.sh run R3                        # un escenario
./chaos.sh run C1 --secs 120 --no-alerts # con duración recortada
./chaos.sh all --only R3,C1,M2,D2        # varios en secuencia, con resumen final
./chaos.sh all --skip S1,D3              # todos menos algunos (más de 2 horas)
./chaos.sh cleanup                       # rollback de emergencia (idempotente)
```

Opciones comunes: `--secs N` (duración de la inyección), `--pre N` / `--post N` (medición antes/después, por defecto 30 s y 60 s), `--url URL`, `--no-alerts`, `--alerts`.

Códigos de salida: `0` hipótesis cumplida, `1` alguna aserción falló, `2` el escenario no pudo ejecutarse (p. ej. el sistema no estaba sano antes de empezar).

## Cómo funciona cada ejecución

1. **Estado estable:** todos los deployments `Ready` y 20 requests sin error. Si no se cumple, aborta: no se inyecta caos en un sistema que ya está roto.
2. **Probe de disponibilidad** en segundo plano, sobre la UI (`/`, `/api/drug`, `/api/pharmacy`, `/api/users`), igual que `zero-downtime-test.sh`. Cuenta como fallo un `000` (conexión/timeout) o un `5xx`. Un `4xx` o `429` significa que el componente respondió.
3. **Fases:** `antes` (línea base) -> `durante` (fallo activo) -> `despues` (recuperación). Se calcula disponibilidad, p50 y p95 por fase y por ruta.
4. **Rollback automático:** cada inyección registra su reversión con un `trap`, que se ejecuta aunque el script falle o se cancele. Las inyecciones además se autolimitan (`timeout` dentro del pod, escalado con anotación de respaldo, `sleep` + reversión dentro del contenedor efímero de red).
5. **Aserciones y alertas:** se comprueban las hipótesis y, vía la API de Grafana, si las alertas esperadas se dispararon (con su tiempo de detección, MTTD).
6. **Informe:** `reports/<ID>-<fecha>.md` con hipótesis, parámetros, resultado de cada aserción, tabla de disponibilidad/latencia por fase y hallazgos.

### Alertas y duraciones

Las alertas de Grafana usan `rate(...[5m])` o z-scores sobre la última hora, con `for` de 1 a 5 min y evaluación cada minuto, así que **solo llegan a dispararse con las duraciones por defecto**. Si se pasa `--secs`, las aserciones de alertas se marcan `SKIP` (salvo `--alerts`). `--no-alerts` las omite siempre.

**Resultado de la verificación contra Grafana** (duraciones por defecto, tiempo hasta que la alerta pasó a `firing`):

| Alerta | Escenario | Resultado |
|---|---|---|
| Logins fallidos anómalos | R2 | Disparó a los 161 s (1465 logins, 336 llegaron a users-service) |
| Tráfico anómalo (z-score) | R1 | Disparó a los 238 s (z=5.6). Una segunda ráfaga idéntica dentro de la misma hora **no** dispara (z=2.4): la primera infla la línea base. R1 lo detecta y marca `SKIP` |
| Servicio sin réplicas disponibles | D2 | Disparó a los 148 s |
| CPU de pod cerca del límite | C1 | Con 360 s **no** dispara: el `rate[5m]` supera el 80% solo ~3 min y la regla pide 3 min continuos. Con 600 s (por defecto de C1-C3) **disparó a los 442 s** |
| Circuit breaker abierto | D2, N3 | **No** dispara, como se predijo (hueco: ningún flujo usa los HttpClient con Polly) |
| Tasa de errores 5xx anómala | C4 | Disparó a los 227-278 s (bloqueo largo sobre `Drugs`) |
| Latencia anómala | N1 | **No** dispara aunque los clientes ven 586 ms en vez de 5 ms: mide el tiempo dentro del servidor, no la latencia de red |

## Escenarios

| ID | Qué hace | Hipótesis | Alerta esperada |
|---|---|---|---|
| **R1** | Ráfaga 10 -> 50 -> 200 clientes concurrentes sobre `GET /api/drug` y `/api/pharmacy`. La carga se genera **dentro del nodo** si el acceso es por túnel (`--from-client` para forzar local). `--direct` ataca el gateway con `X-Real-IP` y `X-Forwarded-For` rotativos para intentar eludir el límite | El token bucket anónimo responde 429 (con `Retry-After`) a >= 90% de la ráfaga, nunca 5xx ni timeouts, 2xx dentro de la cota teórica del bucket, y sin bloqueo residual al terminar. Con `--direct`: falsificar cabeceras **no** da más cupo | Tráfico anómalo (SKIP si la última hora ya tuvo una ráfaga) |
| **R2** | Ataque dirigido a una cuenta (24 intentos) + spraying de `POST /api/login` con credenciales aleatorias (5/s, 360 s), mientras el dueño legítimo intenta entrar | La cuenta atacada se bloquea con backoff (`X-RateLimit-Bucket: account`), el spraying lo frena el presupuesto de fallos por IP, jamás 5xx ni 2xx, y **el dueño legítimo sigue entrando 6 de 6 veces** | Logins fallidos anómalos |
| **R3** | 21 payloads malformados o maliciosos: JSON inválido, body de 2 y 35 MB, anidado de 3000 niveles, SQLi/XSS, URL de 12 KB, path traversal, métodos no permitidos | Siempre 4xx; nunca 5xx ni conexión caída; ningún pod se reinicia | - |
| **R4** | Inundación anónima sostenida (40 clientes, 90 s) desde una IP mientras, **desde esa misma IP**, un usuario autenticado y un anónimo hacen 1 request por segundo | El autenticado recibe siempre 200 (cubo propio), el anónimo 429 con `Retry-After`, y al cesar la inundación el anónimo se recupera en segundos | - |
| **C1** | 2 bucles de CPU en **una** réplica del gateway (límite 500m) | La otra réplica sostiene el servicio (>= 99%) | CPU de pod cerca del límite |
| **C2** | CPU en **todas** las réplicas de pharmacy | Sube la latencia de las rutas de pharmacy; `/api/users` no se ve afectado | CPU de pod cerca del límite |
| **C3** | CPU en `pharmago-db` | Sube la latencia de las rutas con BD; la UI sigue disponible | CPU de pod cerca del límite |
| **C4** | Satura la BD **desde dentro de SQL Server**: (1) un `ALTER TABLE` abierto sobre `Drugs` mantiene un bloqueo de esquema (Sch-M) mientras 60 clientes autenticados piden `/api/drug`; (2) 8 consultas CPU-bound concurrentes (hash SHA-512 por fila). Fases `bloqueo`, `recuperacion`, `pesadas` | El bloqueo debería dejar sin servicio solo a `/api/drug`, sin reiniciar pods; con el motor saturado sube la latencia sin 5xx en la UI. **Hoy falla**: el bloqueo se propaga a `/api/pharmacy` y reinicia pods (hallazgo 15) | Tasa de errores 5xx anómala |
| **M1** | Lleva un pod de users al ~88% de `limits.memory` (retiene RAM real con `tail`) | Sin OOMKill, sin reinicios, >= 99% | Memoria de pod cerca del límite |
| **M2** | Una réplica de users intenta retener más memoria que su `limits.memory` (125%) | El kernel hace cumplir el límite (`oom_kill` del cgroup +1) sin afectar al servicio (>= 99%). El OOMKill **del contenedor** no se puede forzar desde dentro del pod (ver nota) | - |
| **S1** | Escribe en el disco efímero del nodo hasta dejar ~14% libre, con tope de seguridad | La app no se degrada y no se desaloja ningún pod | Disco del nodo casi lleno |
| **S2** | Agota el archivo de datos de SQL Server (`MAXSIZE` = tamaño actual) hasta el error 1105 | Las escrituras fallan, las lecturas siguen en 200, ningún pod se reinicia | - |
| **S3** | Inunda de logs un pod de users (200 líneas/s) hacia Fluent-bit -> Elasticsearch | Sin impacto a usuarios; llega al menos el 50% de las líneas | - |
| **N1** | `tc netem` con 500 ms de latencia y 20% de pérdida en las réplicas de users | Sube la latencia de `/api/users` para los clientes, pharmacy no cambia, sin caída total | Latencia anómala **no** salta (punto ciego) |
| **N2** | `iptables` corta gateway -> pharmacy (`--mode drop` o `reject`) | Fallan solo las rutas de pharmacy; MTTR tras restaurar <= 120 s | Saturación de requests (modo drop) |
| **N3** | `iptables` corta users/pharmacy -> BD (TCP 1433) | Fallan las rutas con BD, la UI no, las apps se recuperan solas | Circuit breaker **no** salta (ver hallazgos) |
| **D1** | Elimina un pod aleatorio de las 4 apps cada 30 s (`--abrupt`: sin gracia) | 100% de disponibilidad (95% en modo abrupto) | - |
| **D2** | Escala pharmacy a 0 durante 240 s | Fallan solo sus rutas; MTTR <= 90 s | Servicio sin réplicas disponibles |
| **D3** | Baja la BD a 0 y la vuelve a levantar | 5xx en rutas con BD; recuperación sin reiniciar apps; los datos persisten | - |
| **D4** | Baja el OTel Collector | Los usuarios no notan nada; Prometheus pierde las métricas de aplicación | - |
| **G1** | Tráfico + CPU en un gateway + latencia/pérdida en users, a la vez | Degrada pero sigue sirviendo (>= 95%) | CPU de pod, Latencia anómala |

Duraciones por defecto: `./chaos.sh list`. Las pruebas de red agregan unos minutos la primera vez (descarga de la imagen). Los escenarios con alerta de CPU duran 10 min; `./chaos.sh all` completo tarda unas 3 horas.

## Seguridad de los experimentos

- **S1 no llena el disco del host a ciegas:** calcula cuánto habría que escribir para llegar al umbral, nunca deja menos del 11% libre (kubelet desaloja pods al 10%) y aplica un tope absoluto (`--max-mb`, 4096 por defecto). En un nodo de 1 TB llegar al umbral exigiría ~750 GB: el escenario llena hasta el tope, comprueba que la app no se degrada y marca la aserción de alerta como `SKIP` en vez de simularla.
- **R1, R2 y R4 ya no dejan bloqueos residuales:** el token bucket se recupera en segundos, así que no hace falta reiniciar el gateway entre escenarios (antes el tope de 1000/hora dejaba a los usuarios reales con 429 durante el resto de la hora). R2 deja bloqueada unos minutos la cuenta ficticia `objetivo-chaos`, que no existe.
- **S2 solo toca el archivo de datos de `PharmaDb`**, no el disco del nodo. La tabla de relleno se elimina y se restauran `MAXSIZE`/`FILEGROWTH` originales.
- **Red:** las reglas viven en el namespace de red del pod y se aplican desde un contenedor efímero. No se modifica ningún Deployment. Los contenedores efímeros quedan listados en el pod hasta que este se recrea (Kubernetes no permite quitarlos).
- **Escalados (D2, D3, D4):** las réplicas originales se guardan en la anotación `chaos.pharmago.io/original-replicas` del Deployment para poder restaurarlas con `./chaos.sh cleanup` aunque el script haya muerto.
- Ejecútalos solo contra un entorno de pruebas. D3 deja la BD fuera de servicio unos minutos y R1/R2 consumen el rate limit.

- **C4 modifica el esquema de forma temporal:** abre una transacción con `ALTER TABLE dbo.Drugs ADD chaos_lock` y la revierte con `ROLLBACK` (el DDL es transaccional). El rollback mata las sesiones y comprueba que la columna no quede; `./chaos.sh cleanup` también lo hace si se corta de golpe. Úsalo solo en un entorno de pruebas: durante unos minutos `/api/drug` deja de responder y los pods de `pharmacy-service` pueden reiniciarse.
- **M2 y el OOMKill:** desde dentro del pod no se puede forzar que muera el contenedor: el OOM killer elige al proceso con mayor puntaje (el inyectado) y el cgroup es de solo lectura, así que no se puede activar `memory.oom.group`. Un intento previo con `oom_score_adj` en PID 1 no compensa los 400 MB residentes del proceso inyectado. M2 mide lo que sí es verificable (el kernel hace cumplir el límite y el servicio no se ve afectado) y deja el reinicio por OOMKilled como `SKIP`.

## Hallazgos del diseño actual

Salieron de leer el código al diseñar los experimentos y los escenarios los verifican:

1. **El circuit breaker de Polly nunca se ejecuta.** `PharmacyServiceClient` y `UsersServiceClient` están registrados en `ServiceFactory.cs` pero ningún controller ni manager los invoca. La alerta *Circuit breaker abierto* no salta ni cuando pharmacy está caído (D2) ni con la BD inalcanzable (N3). D2 y N3 lo verifican con `forbid_alert`.
2. **La BD no tiene reintentos.** `UseSqlServer` no usa `EnableRetryOnFailure` en ningún servicio, así que una caída de la BD (D3, N3) se traduce directamente en 5xx hasta que vuelve. `README-k8s.md` decía lo contrario y se corrigió.
3. **`/health` no verifica la BD** (`AddHealthChecks()` sin checks). Con la BD caída o inalcanzable los pods siguen `Ready` y Kubernetes les sigue enviando tráfico que falla (D3, N3). Un check con `AddDbContextCheck` los sacaría del balanceo.
4. **[Corregido] El rate limit se eludía falsificando cabeceras.** Llegando directo al gateway con `X-Real-IP` rotativo pasaban 400 de 400 requests sin un 429. Ahora el gateway ignora `X-Real-IP` y lee `X-Forwarded-For` solo si el peer es un pod de nginx: con cabeceras rotativas pasan 527 de 28 200 (R1 `--direct`). Un primer intento, confiar en toda la red de pods (CIDR), **seguía fallando** (400 de 400) porque el puente del nodo cae dentro de ese rango; solo se descubrió probando contra el cluster. Ver [`RATE_LIMITING_GUIDE.md`](../Backend/RATE_LIMITING_GUIDE.md), D5.
5. **Las alertas de `pharmago_*` quedan ciegas si cae el OTel Collector** (D4): usan `noDataState: OK` y no hay una alerta sobre `up{job="otlp-collector"}`.
6. **El PodDisruptionBudget no protege contra el escalado a 0** (D2); solo contra evictions y drains.
7. **La alerta *Latencia anómala* es ciega a la latencia de red** (N1). `pharmago_http_request_duration` se mide en el middleware del servidor, antes de que el retardo llegue al cable: con 500 ms de `tc netem` los clientes vieron 586 ms de mediana en `/api/users` mientras la métrica del servidor se quedó en ~0 ms (ratio 1.0 contra su línea base). Para detectar degradación de red hace falta medir desde el cliente (`http_client_duration` del gateway) o con probes externos.
8. **[Corregido] El rate limit por IP bloqueaba a todos los usuarios que comparten IP.** Una ráfaga desde una sola IP (R1 mandó ~50 000) dejaba con 429 al resto de ese origen durante lo que quedaba de la hora. Ahora hay un token bucket por identidad: con la IP inundada, el usuario autenticado de esa IP recibió 140 de 140 respuestas 200 y el anónimo se recuperó en 1 s (R4). Ver la guía, D1, D2 y D4.
9. **[Corregido] El rate limit se eludía por completo yendo directo al gateway.** Era el mismo defecto que el 4. Ver ese punto.
10. **El z-score de *Tráfico anómalo* se enmascara a sí mismo.** Una ráfaga de 135 req/s dio z=5.6 la primera vez y z=2.4 la segunda, 25 minutos después, porque la primera ya estaba en la ventana de 1 h. La alerta pierde sensibilidad justo cuando hay ataques repetidos.
11. **La alerta de CPU necesita ~10 min de saturación** (C1): `rate[5m]` suaviza la subida y `for: 3m` exige 3 min por encima del 80%. Una saturación de 6 min no dispara nada.
12. **El túnel de `minikube service` (driver Docker en Windows) se satura con ~100 conexiones** y genera timeouts que no son de la aplicación: desde dentro del nodo, 6000 requests con 200 clientes dieron 6000 respuestas correctas en `/` y cero errores en `/api/drug`. Por eso R1 genera la carga dentro del nodo.
13. **`minikube image load` no reemplaza una imagen con la misma etiqueta** (`:latest`). Al desplegar el rate limiting los pods siguieron con el binario viejo sin ningún aviso (el endpoint nuevo respondía 404). Hay que cargar con `docker save <imagen> | minikube image load -` y comprobar el contenido del pod. Documentado en `k8s/README-k8s.md`.
14. **Seguridad del login, sin relación con el rate limit:** las contraseñas se comparan en texto plano, el login distingue "usuario no existe" (404) de "clave incorrecta" (400), lo que permite enumerar cuentas, y las sesiones no caducan ni hay logout (el login reutiliza siempre el mismo token). El backoff por cuenta frena la fuerza bruta, pero no corrige nada de esto.
15. **Un bloqueo largo sobre una tabla tumba el servicio entero y reinicia los pods (C4).** Un `ALTER TABLE` abierto sobre `Drugs` dejó 128-175 requests bloqueadas dentro de SQL Server y 146-184 conexiones de la app a la BD (el pool es de 100 por réplica). `/api/pharmacy`, que **no** usa esa tabla, falló en 20 de 24 requests, y **los dos pods de `pharmacy-service` fallaron la liveness probe** (`/health` agotó el timeout) y se reiniciaron a la vez, 2 veces cada uno. Causa: las consultas son síncronas y esperan hasta el timeout de comando de SqlClient (30 s) ocupando un hilo y una conexión; con decenas de requests bloqueadas el proceso se queda sin hilos y ni siquiera atiende `/health`. Reiniciar no arregla el bloqueo, solo suma una caída. La alerta de 5xx sí saltó (227-278 s) y la recuperación fue de 1-14 s tras liberar el bloqueo. Mitigaciones posibles (no implementadas): `Command Timeout` corto (p. ej. 5 s) y `SET LOCK_TIMEOUT`, `Max Pool Size`, acceso a datos `async`, timeouts por request en YARP, y una liveness probe más tolerante o independiente de la carga.
16. **La BD usa `READ_COMMITTED_SNAPSHOT = 1`:** los lectores no se bloquean con bloqueos exclusivos de fila o tabla, solo con bloqueos de esquema (DDL). Es lo que hace que una migración o un `ALTER TABLE` largo sobre una tabla caliente sea el caso peligroso, y lo que usa C4 (la app aplica migraciones EF al arrancar con `MigrateDatabase()`).

## Archivos

```
chaos.sh                  orquestador: list | run | all | cleanup
lib/common.sh             librería: probe, aserciones, inyecciones, alertas de Grafana, informes
scenarios/<id>-*.sh       un script por escenario
zero-downtime-test.sh     verificación de alta disponibilidad durante rollouts (independiente)
reports/                  informes generados por cada corrida
```

## Solución de problemas

- **"El sistema no está sano, no se inyecta caos"**: espera a que los pods estén `Ready` (`kubectl get pods -n pharmago`).
- **N* se queda en "Preparando herramientas de red"**: el nodo no puede descargar `nicolaka/netshoot`. Prueba `minikube ssh -- docker pull nicolaka/netshoot` o define `NETSHOOT_IMAGE` con otra imagen que traiga `tc` e `iptables`.
- **Alertas en SKIP con "Grafana no accesible"**: el script hace port-forward a `svc/grafana`; revisa que el pod esté `Ready`. Credenciales por defecto `admin:admin` (`GRAFANA_AUTH` para cambiarlas).
- **Quedó algo a medias tras un corte brusco** (cierre de la terminal, apagón): `./chaos.sh cleanup`.
