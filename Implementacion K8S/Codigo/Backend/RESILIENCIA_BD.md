# Resiliencia del acceso a la base de datos

users-service y pharmacy-service acceden a la BD a través de un decorador de repositorio que aplica **timeout corto de comando, circuit breaker por entidad y retry con equal jitter**. Este documento explica qué hace, cómo se configura y **por qué se tomó cada decisión**. El código está en [`PharmaGo.DataAccess/Resilience/`](PharmaGo.DataAccess/Resilience/).

> El rate limiting del gateway ([`RATE_LIMITING_GUIDE.md`](RATE_LIMITING_GUIDE.md)) protege lo que **entra** al sistema. Esto protege lo que **sale** hacia la BD. Son complementarios y no se pisan: un 429 lo genera el gateway antes de reenviar la request, así que nunca llega a esta capa.

---

## 1. Qué problema había (medido en el cluster)

| Escenario | Qué se midió |
|---|---|
| **C4** (un `ALTER TABLE` abierto sobre `Drugs`, 60 clientes pidiendo `/api/drug`) | Cada request esperaba el timeout de comando de SqlClient (**30 s**) ocupando un hilo y una conexión del pool (100 por réplica): 128-175 requests bloqueadas en SQL y 146-184 conexiones de la app. `pharmacy-service` se quedó sin recursos, **`/health` dejó de responder, los dos pods fallaron la liveness y se reiniciaron a la vez**, y `/api/pharmacy`, que no usa esa tabla, cayó al 17% de disponibilidad. |
| **D3 / N3** (BD caída o inalcanzable) | Sin reintentos (`UseSqlServer` sin `EnableRetryOnFailure`): todo corte de la BD, por breve que fuera, llegaba al usuario como 5xx. |
| Circuit breaker de Polly existente | Registrado para los `HttpClient` entre servicios, pero **ningún flujo los usa**: el breaker nunca se ejecutaba. |

## 2. Cómo funciona

```
request ─► controller ─► manager ─► IRepository<T>
                                         │  (decorador)
                                         ▼
                              ResilientRepository<T>
                                         │
              lectura:  retry ( breaker_T ( consulta ) )        escritura (Save):  breaker_T ( Save )
                                         │
                                         ▼
                           EF Core ─► SqlClient (CommandTimeout 5 s)
```

- Cada entidad (`Drug`, `Pharmacy`, `User`...) tiene **su propio circuit breaker** (`db:Drug`...).
- Si algo falla por disponibilidad de la BD, el decorador lanza `DatabaseUnavailableException`; los `ExceptionFilter` la traducen a **HTTP 503 con `Retry-After`**.
- Los errores de negocio (404, 400, constraints) pasan intactos y **no cuentan** para el breaker.

---

## 3. Decisiones de diseño y por qué

### D1. Timeout corto de comando (5 s) y de conexión (5 s)
- **Por qué:** es la palanca que realmente hace fallar rápido. Las consultas de EF son **síncronas**, así que solo SqlClient puede cortarlas: un timeout de Polly no interrumpe una llamada bloqueante. El valor por defecto (30 s) es lo que dejaba un hilo y una conexión ocupados medio minuto por request bloqueada.
- **Por qué 5 s:** las consultas de PharmaGo son lecturas simples sobre tablas pequeñas (decenas de ms medidos). 5 s deja una holgura de más de 50x sin permitir que una request bloqueada ocupe recursos durante tanto tiempo. Es configurable.
- **Connect Timeout:** el de SqlClient es 15 s; se baja a 5 s **solo si el connection string no define uno explícito**.
- **Las migraciones quedan exentas:** `MigrateDatabase()` fija su propio timeout (300 s) porque una migración legítima puede tardar más que una consulta normal.

### D2. Un circuit breaker por entidad, no uno para toda la BD
- **Por qué:** el objetivo es que un problema en una tabla **no arrastre al resto**. Con un breaker por entidad, el bloqueo de `Drugs` abre solo el breaker de `Drug` y las rutas que usan otras tablas siguen respondiendo. Un breaker global las tumbaría junto con la afectada.
- **Cuándo se abre:** tras **5 fallos manejados consecutivos**; queda abierto **15 s** y luego deja pasar una request de prueba (half-open). Un éxito intermedio reinicia la cuenta.
- **Qué cuenta como fallo:** timeout de comando, fallos transitorios de conexión, pool agotado. **No cuentan** los errores de negocio, de constraint ni de sintaxis: fallarían igual y no indican un problema de la BD.
- **Limitación conocida:** `PharmacyRepository.GetOneByExpression` hace `Include("Drugs")`, así que el *detalle* de una farmacia sí depende de la tabla `Drugs` y se ve afectado por su breaker. Es esperable: esa consulta realmente toca esa tabla.

### D3. Retry con *equal jitter*, solo para lecturas con fallos transitorios de conexión
- **Fórmula:** `techo = min(tope, base · 2^(intento-1))` y `espera = techo/2 + aleatorio(0, techo/2)`. Con base 200 ms y tope 2 s: ~[100,200], [200,400], [400,800] ms.
- **Por qué equal jitter y no los otros:**
  - *Sin jitter:* dos pods que fallan en el mismo instante reintentan juntos, y vuelven a golpear a la vez a una BD que apenas se está recuperando.
  - *Full jitter* (aleatorio entre 0 y el techo): puede dar ~0 ms y reintentar al instante contra un servicio que se está reiniciando.
  - *Equal jitter:* garantiza una **espera mínima** (la mitad fija) y aun así **reparte** los reintentos en el tiempo (la mitad aleatoria).
- **Por qué solo lecturas:** `Save()` **no se reintenta**. No es idempotente y un timeout no dice si el commit llegó a aplicarse; reintentar podría duplicar una compra.
- **Por qué solo fallos transitorios de conexión:** un **timeout de comando o un pool agotado no se reintentan**. Reintentar contra una tabla bloqueada multiplica la espera y la carga justo cuando más daño hace (es exactamente lo que tumbó al servicio en C4). El caso que sí resuelve es el reinicio breve de la BD: las conexiones viejas del pool están rotas, la primera consulta por conexión falla, y el reintento toma una conexión nueva.
- **Orden `retry( breaker( consulta ) )`:** cada intento cuenta para el breaker, así que un corte real lo abre rápido, y el retry **no reintenta** un circuito abierto (falla en milisegundos).

### D4. Un decorador sobre `IRepository<T>`
- **Por qué:** es el único punto por el que pasa todo el acceso a datos de los dos servicios (verificado: no hay uso de `DbContext` fuera de los repositorios, y hasta los filtros de autorización usan `IRepository`). Los managers, controllers y filtros no necesitan saber que la resiliencia existe.
- **Las consultas se materializan dentro de la política** (`GetAllByExpression` y `GetAllBasicByExpression` hacen `ToList()`). EF las devuelve **diferidas**: sin esto el SQL correría al serializar la respuesta, fuera del alcance de Polly y del `ExceptionFilter`, y el fallo escaparía como un 500 sin ser contado por el breaker. El tipo devuelto no cambia (`IEnumerable<T>`).

### D5. Por qué Polly y no `EnableRetryOnFailure` de EF
`EnableRetryOnFailure` no tiene circuit breaker, no aísla por entidad, no distingue lecturas de escrituras y su jitter no es configurable. Polly ya estaba en el proyecto (7.2.2, vía `Microsoft.Extensions.Http.Polly`) y permite expresar las cuatro decisiones anteriores.

### D6. Clasificación de errores aislada y probable (`IDbFailureClassifier`)
La lista de errores de SQL Server está en una clase propia. Recorre la cadena de excepciones internas (EF envuelve en `DbUpdateException` / `InvalidOperationException`). `SqlException` no se puede construir fuera de SqlClient, así que la clasificación se prueba con un punto de extensión (`TryGetSqlNumber`).

### D7. Respuestas HTTP honestas
- **503 + `Retry-After`** (en lugar de un 500 genérico): le dice al cliente que es temporal y cuándo reintentar.
- **`AuthorizationFilter` de pharmacy:** sus `catch { return false; }` convertían un fallo de BD en **401 "token inválido"**, que es engañoso (y haría que el frontend cierre la sesión de un usuario válido). Ahora deja pasar `DatabaseUnavailableException`.
- **Login y compras:** una BD caída ya **no cuenta como login/compra "fallido"** (se registra como `unavailable`). Si contara, con el breaker abierto los fallos serían rapidísimos y dispararían la alerta de *fuerza bruta* por una simple caída de la BD. El gateway tampoco los cuenta (solo 400/401/403/404 son fallos de credenciales).

### D8. Observabilidad: se reutiliza la alerta existente
El breaker publica en el gauge `pharmago_circuit_breaker_state{target="db:Drug"}`, el mismo que usa la alerta **"Circuit breaker abierto"** de Grafana. Esa alerta, que hasta ahora nunca podía dispararse, pasa a cubrir la BD **sin cambios en Grafana**. Se añade el contador `pharmago_db_retries_total{target}`: el existente se llama `..._http_client_...` y sería engañoso para la BD. `DataAccess` no depende de la librería de métricas: cada servicio implementa `IDbResilienceObserver` y la reenvía.

---

## 4. Parámetros

Sección `Database:Resilience` de la configuración, o variables de entorno con `__` (`Database__Resilience__CommandTimeoutSeconds=10`).

| Parámetro | Por defecto | Para qué |
|---|---|---|
| `CommandTimeoutSeconds` | 5 | Timeout de cada consulta |
| `ConnectTimeoutSeconds` | 5 | Timeout de conexión (si el connection string no lo define) |
| `MigrationTimeoutSeconds` | 300 | Timeout de las migraciones al arrancar |
| `BreakerFailures` | 5 | Fallos consecutivos para abrir el breaker de una entidad |
| `BreakDurationSeconds` | 15 | Tiempo abierto antes de probar de nuevo |
| `RetryCount` | 3 | Reintentos de una lectura transitoria |
| `RetryBaseDelayMs` / `RetryMaxDelayMs` | 200 / 2000 | Base y tope del equal jitter |

## 5. Cómo probarlo

- **Tests unitarios** (`PharnaGo.Test/DataAccess.Test/Resilience`, 50 tests): equal jitter (cotas y espera mínima), clasificador, apertura/cierre del breaker, aislamiento por entidad, que el timeout y el pool agotado no se reintenten, que `Save` no se reintente, que los errores de negocio no cuenten, y que una consulta diferida se capture dentro de la política.
  ```bash
  dotnet test Backend/PharnaGo.Test/PharmaGo.Test.csproj --filter "FullyQualifiedName~DataAccess.Test.Resilience"
  ```
- **Escenarios de caos** (contra el cluster): `C4` (bloqueo largo + consultas pesadas), `D3` (BD caída) y `N3` (red cortada hacia la BD). Ver [`chaos-engineering/README.md`](../chaos-engineering/README.md).

## 6. Resultados medidos en el cluster

Mismo escenario (C4: bloqueo de esquema de 150 s sobre `Drugs` con 60 clientes autenticados), antes y después:

| | Antes | Después |
|---|---|---|
| `/api/pharmacy` (no usa `Drugs`) | falló 20 de 24 (17% de disponibilidad) | **67 de 67 OK** |
| `/api/drug` (tabla bloqueada) | esperaba 30 s (p95 5012 ms) | falla con 503 en **p50 45 ms, p95 112 ms** |
| Requests esperando dentro de SQL | 128-175 | **0** |
| Conexiones de la app a la BD | 146-184 | **31** |
| Reinicios por liveness | 2 por pod, ambos a la vez | **0** |
| Alerta *Circuit breaker abierto* | imposible | disparó a los 47 s |

Caída de la BD (D3, 90 s) y corte de red hacia la BD (N3, 180 s): las rutas con BD fallan con 503 en **p50 5-6 ms**, 0 reinicios, recuperación sola en 44 s (D3) y 15 s (N3), y la alerta del breaker dispara a los 83 y 96 s.

### Lo que se aprendió al medirlo (y se corrigió)
1. **El error de "BD inalcanzable" llega con `SqlException.Number = 0`**, no con un número conocido: la primera versión del clasificador lo dejaba pasar como un 500 crudo. Se añadió un respaldo por mensaje (`network-related or instance-specific error...`) y el registro de cada fallo con su tipo y número (`Fallo de BD en db:Drug: SqlException (SQL 0)`), que permite ampliar la lista con datos en lugar de suponer.
2. **El gauge del breaker se quedaba "abierto" para siempre** con la BD sana si la entidad dejaba de recibir tráfico (un breaker solo se cierra con una llamada exitosa, y el valor solo se actualizaba en las transiciones). Daría una alerta falsa. Ahora el gauge consulta el estado real del breaker en cada medición: pasa a half-open (0.5) cuando vence el tiempo, aunque nadie llame.
3. **La aserción "hubo requests bloqueadas dentro de SQL" de C4 dejó de tener sentido**: la mejora hace que ese número baje de ~150 a 0 (el breaker corta antes de llegar a la BD). Ahora se comprueba que el bloqueo Sch-M estaba retenido.

### Qué no se llegó a ejercitar en el cluster
Los **reintentos con equal jitter no se activaron** en D3 ni en N3: el error de conexión rechazada/inalcanzable está clasificado como "cuenta para el breaker pero no se reintenta" (el mismo mensaje cubre una espera de conexión agotada de 5 s y reintentarla multiplicaría la espera), y una caída de 90 s no se salva con reintentos de ~1 s de ventana. El retry y su jitter están cubiertos por tests unitarios (cotas del jitter, que no se reintente un timeout ni un `Save`), pero para verlos actuar en vivo haría falta un reseteo de conexiones de menos de un segundo, que no hay un escenario que reproduzca.

## 7. Limitaciones conocidas

| Limitación | Detalle |
|---|---|
| Estado por proceso | Cada réplica tiene sus propios breakers: pueden estar en estados distintos durante unos segundos. Es aceptable porque cada una protege su propio pool y sus propios hilos. |
| Consultas legítimamente largas | Un `CommandTimeout` de 5 s cortaría una consulta válida que tarde más (p. ej. una exportación grande). Hoy ninguna lo necesita; si aparece, subir `CommandTimeoutSeconds` o hacer una excepción puntual. |
| Escrituras sin reintento | Un corte breve durante un `Save()` llega al usuario como 503 aunque la BD se recupere un segundo después. Es la elección segura frente a duplicar datos. |
| Los `HttpClient` entre servicios | Siguen sin usarse (los servicios comparten la BD y leen las tablas del otro); su breaker existente no se toca. |
