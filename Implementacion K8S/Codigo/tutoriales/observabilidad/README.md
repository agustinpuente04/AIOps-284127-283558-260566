# Observabilidad: logs, trazas y métricas

## Requisito

> La aplicación debe contar con: logs estructurados en JSON, trazas de acuerdo a OTLP, métricas a nivel de pod/contenedor y nodo físico (CPU, memoria, storage y red). Las métricas deben recolectarse con OTLP hacia Prometheus, visualización con Grafana. Los logs deben poder consultarse en alguna herramienta de manejo de logs como Kibana con Elasticsearch.

## Estado: cumplido, con dos observaciones

| Punto del requisito | Estado | Dónde |
|---|---|---|
| Logs estructurados en JSON | ✅ Cumple | [§1](#1-logs-estructurados-en-json) |
| Trazas de acuerdo a OTLP | ⚠️ Se emiten en OTLP; no hay backend de trazas | [§2](#2-trazas-otlp) |
| Métricas de nodo físico: CPU, memoria, storage, red | ✅ Cumple, en dashboard | [§3](#3-métricas-de-podcontenedor-y-nodo) |
| Métricas de pod: CPU, memoria (y % del límite), storage (I/O de disco) | ✅ Cumple, en dashboard | [§3](#3-métricas-de-podcontenedor-y-nodo) |
| Métricas de pod: red | ⚠️ El cAdvisor del kubelet no las expone en este Minikube; hay un cAdvisor standalone listo, sin aplicar | [§3](#3-métricas-de-podcontenedor-y-nodo) |
| Métricas vía OTLP → Prometheus (incluye las nuevas de negocio, saturación, resiliencia y runtime) | ✅ Cumple | [§4](#4-métricas-de-aplicación-otlp--prometheus--grafana) |
| Visualización con Grafana (3 dashboards) | ✅ Cumple | [§4](#4-métricas-de-aplicación-otlp--prometheus--grafana) |
| Al menos 5 alertas de anomalías en Grafana | ✅ Cumple: 10 reglas, 6 probadas disparando | [§5](#5-alertas-de-detección-de-anomalías-grafana) |
| Logs consultables en Kibana + Elasticsearch | ✅ Cumple (verificado) | [§6](#6-logs-en-kibana--elasticsearch) |

Las observaciones (⚠️) están explicadas en [§7](#7-limitaciones-conocidas). No bloquean el requisito, pero conviene que el equipo las conozca.

## Arquitectura

```
┌─────────────┐  ┌──────────────┐  ┌───────────────┐
│ API Gateway │  │ Users Service│  │ Pharmacy Svc   │   (stdout: 1 línea JSON por log)
└──────┬──────┘  └──────┬───────┘  └───────┬────────┘
       │ OTLP/gRPC (métricas + trazas)     │
       └───────────────┬────────────────────┘
                        ▼
                ┌───────────────┐
                │ OTel Collector │── traces ──► (debug: solo stdout del propio collector)
                └───────┬────────┘
                        │ expone :8889 en formato Prometheus
                        ▼
   ┌──────────┐   ┌────────────┐   ┌─────────────────┐   ┌───────────┐
   │  cAdvisor │──►│            │   │                  │   │           │
   │ (por nodo)│   │ Prometheus │──►│ Grafana (:3000)  │   │           │
   ├──────────┤   │  (:9090)   │   │  dashboards      │   │           │
   │node-export│──►│            │   └──────────────────┘   │           │
   │ (por nodo)│   └────────────┘                          │           │
   └──────────┘                                            │           │
                                                             │           │
   Pods (stdout JSON) ──► Fluent Bit (DaemonSet) ──────────►│ ES (:9200)│──► Kibana (:5601)
                                                             └───────────┘
```

## 1. Logs estructurados en JSON

Cada servicio .NET (gateway, users, pharmacy) tiene en su `appsettings.json`:

```json
"Logging": {
  "Console": {
    "FormatterName": "json",
    "FormatterOptions": { "IncludeScopes": true, "TimestampFormat": "yyyy-MM-ddTHH:mm:ss.fffZ" }
  }
}
```

Esto hace que **toda** la salida de `ILogger` (la propia de ASP.NET Core y la nuestra) sea una línea JSON por evento, no texto plano.

Sobre esa base hay dos niveles de logging:

1. **Automático, por request** — `MetricsMiddleware` ([Instrumentation/MetricsMiddleware.cs](../../Backend/Instrumentation/MetricsMiddleware.cs)) loguea una línea por cada HTTP request (`"HTTP {Method} {Endpoint} responded {StatusCode} in {ElapsedMs} ms"`), con éxito o excepción. No requiere que nadie lo llame a mano.
2. **Manual, de negocio** — `IStructuredLogger` ([Instrumentation/StructuredLogger.cs](../../Backend/Instrumentation/StructuredLogger.cs)) para eventos que importan más allá del request HTTP (login, compras). Se inyecta y se usa así (ejemplo real en [LoginController.cs](../../Backend/PharmaGo.UsersService/Controllers/LoginController.cs)):

   ```csharp
   _structuredLogger.LogInformation("Login succeeded", new Dictionary<string, object> {
       ["pharma_biz"] = "login_success",
       ["component"] = "LoginController",
       ["operation"] = "login",
       ["outcome"] = "success",
       ["user_name"] = authorization.UserName,
       ["user_id"] = authorization.UserId
   });
   ```

   `IStructuredLogger` agrega automáticamente `timestamp_utc`, `level`, `service` y `correlation_id` a cada evento (no hay que pasarlos a mano), y además le pega al mensaje un token de una sola palabra (`EVTLGOK`, `EVTLGFL`, etc.) para que sea buscable sin comodines en Kibana. Guía paso a paso para agregar un log nuevo: [tutoriales/logging/como-agregar-log-estructurado.md](../logging/como-agregar-log-estructurado.md) *(ese tutorial tiene un ejemplo con campos `status`/`message`, más viejo que el de `LoginController.cs`; el código real usa `pharma_biz`/`component`/`operation`/`outcome` — guiarse por el código, no por ese ejemplo puntual)*.

**`correlation_id`**: el gateway genera un GUID por request entrante y lo propaga en el header `X-Correlation-ID` a los microservicios ([CorrelationIdMiddlewareExtensions.cs](../../Backend/Instrumentation/CorrelationIdMiddlewareExtensions.cs)). Verificado: el mismo GUID aparece en el log del gateway y en el log de negocio del servicio que atendió la request — permite reconstruir una request de punta a punta en Kibana.

**Verificado el 2026-11-06** contra un cluster real: hice login correcto e incorrecto por el gateway y confirmé en Elasticsearch que ambos eventos llegan con todos los campos esperados (detalle completo en [§6](#6-logs-en-kibana--elasticsearch)).

## 2. Trazas OTLP

El gateway y los dos microservicios inicializan tracing de OpenTelemetry:

```csharp
// PharmaGo.ApiGateway/Program.cs y *.Factory/TelemetryExtensions.cs
.WithTracing(tracingBuilder => tracingBuilder
    .AddAspNetCoreInstrumentation(opts => opts.RecordException = true)
    .AddHttpClientInstrumentation()
    .AddOtlpExporter(opts => opts.Endpoint = new Uri("http://otlp-collector:4317")));
```

Cada request genera un *span* con `TraceId`/`SpanId`/`ParentId` (esos mismos IDs aparecen también en los logs JSON, en el scope de ASP.NET — se puede cruzar un trace con sus logs). Los spans se exportan por OTLP/gRPC al OTel Collector, que los recibe correctamente (confirmado: pipeline `traces` configurada en [otel-collector-config.yaml](../../k8s/configmaps/otel-collector-config.yaml)).

**Lo que falta:** el pipeline de trazas del collector solo tiene el exporter `debug`, que imprime los spans en el propio stdout del collector y no los persiste en ningún lado:

```yaml
service:
  pipelines:
    traces:
      receivers: [otlp]
      exporters: [debug]   # <- solo logging interno del collector, no hay backend
```

Es decir: el requisito "trazas de acuerdo a OTLP" se cumple en el sentido de que la app **emite** trazas con el protocolo correcto, pero hoy no hay dónde consultarlas (no hay Jaeger, Tempo ni Zipkin desplegado). Para verlas hay que mirar los logs del propio pod `otlp-collector` (`kubectl logs -n pharmago -l app=otlp-collector`), lo cual no es una herramienta de uso real. Si se quiere visualización, falta agregar un backend de trazas (p. ej. Grafana Tempo, que además se integra directo con el Grafana que ya está desplegado) y un exporter en el collector apuntando a él.

## 3. Métricas de pod/contenedor y nodo

| Fuente | Qué mide | Scrapeado por |
|---|---|---|
| **cAdvisor** (integrado en el kubelet de cada nodo) | CPU, memoria, límites e I/O de disco **por pod** | Job `kubernetes-cadvisor` en Prometheus, vía la API del apiserver (`/api/v1/nodes/<nodo>/proxy/metrics/cadvisor`) |
| **node-exporter** (DaemonSet, [node-exporter-daemonset.yaml](../../k8s/deployments/ops/node-exporter-daemonset.yaml)) | CPU, memoria, **disco (espacio e I/O)** y **red**, a nivel del nodo físico | Job `node-exporter` en Prometheus |

El dashboard **"PharmaGo - Infra"** ([grafana-dashboard-infra.yaml](../../k8s/configmaps/grafana-dashboard-infra.yaml)) tiene dos filas. Todas las queries se validaron contra el Prometheus del cluster el 2026-11-07.

| Panel | Recurso | Nivel | Métrica base |
|---|---|---|---|
| CPU % del nodo | CPU | Nodo | `node_cpu_seconds_total` |
| Memoria usada % del nodo | Memoria | Nodo | `node_memory_MemAvailable_bytes / node_memory_MemTotal_bytes` |
| Disco disponible % | Storage | Nodo | `node_filesystem_avail_bytes / node_filesystem_size_bytes` |
| I/O de disco del nodo | Storage | Nodo | `node_disk_read_bytes_total`, `node_disk_written_bytes_total` |
| Red del nodo (RX / TX) | Red | Nodo | `node_network_{receive,transmit}_bytes_total` (sin `lo`, `veth*`, `docker0`, `bridge`) |
| Errores y descartes de red | Red | Nodo | `node_network_*_errs_total`, `node_network_*_drop_total` |
| CPU por pod | CPU | Pod | `container_cpu_usage_seconds_total` |
| Memoria por pod (working set) | Memoria | Pod | `container_memory_working_set_bytes` |
| CPU por pod - % del límite | CPU | Pod | uso / (`container_spec_cpu_quota` / `container_spec_cpu_period`) |
| Memoria por pod - % del límite | Memoria | Pod | working set / `container_spec_memory_limit_bytes` |
| I/O de disco por pod | Storage | Pod | `container_blkio_device_usage_total` (lectura / escritura) |

**Red por pod: limitación del entorno.** En Minikube el runtime es `cri-dockerd` con cgroup v2. En esa combinación, el cAdvisor del kubelet solo reporta los cgroups a nivel pod (sin label `container`). Además, `container_network_*` y `container_fs_usage_bytes` existen solo para la raíz del nodo (`id="/"`), no para cada pod; se verificó en el Prometheus del cluster. Por eso la red se muestra a nivel nodo, y el "storage" por pod se mide como I/O de disco.

Para tener red y filesystem por pod quedó listo un **cAdvisor standalone** ([cadvisor-daemonset.yaml](../../k8s/deployments/ops/cadvisor-daemonset.yaml)), que lee los contenedores directo desde Docker. No está aplicado: es un DaemonSet `privileged` con montajes del host y requiere aprobación explícita (ver [§7](#7-limitaciones-conocidas)).

Cómo agregar un panel nuevo: [tutoriales/como-agregar-metrica-nueva.md](../como-agregar-metrica-nueva.md) y [tutoriales/como-usar-grafana.md](../como-usar-grafana.md).

## 4. Métricas de aplicación: OTLP → Prometheus → Grafana

Las métricas de la app se definen con el SDK de métricas de .NET (`Meter("PharmaGo.CustomMetrics")`). Los nombres de la tabla son los que quedan en Prometheus:

| Métrica | Tipo | Labels | Dónde se registra |
|---|---|---|---|
| `pharmago_http_requests_total` | Counter | `endpoint`, `method`, `status_code` | [MetricsMiddleware.cs](../../Backend/Instrumentation/MetricsMiddleware.cs) (cada request) |
| `pharmago_http_errors_total` | Counter | `endpoint`, `error_type` | MetricsMiddleware |
| `pharmago_http_request_duration_milliseconds` | Histogram | `endpoint`, `method` | MetricsMiddleware |
| **`pharmago_http_requests_in_flight`** (nueva) | UpDownCounter | — | MetricsMiddleware (+1 al entrar, −1 en `finally`) |
| **`pharmago_logins_total`** (nueva) | Counter | `outcome` (success/failed) | [LoginController.cs](../../Backend/PharmaGo.UsersService/Controllers/LoginController.cs) |
| **`pharmago_purchases_total`** (nueva) | Counter | `outcome` | [PurchasesController.cs](../../Backend/PharmaGo.PharmacyService/Controllers/PurchasesController.cs) |
| **`pharmago_purchase_amount`** (nueva) | Histogram | `outcome` | PurchasesController (`TotalAmount` de la compra) |
| **`pharmago_stock_requests_total`** (nueva) | Counter | `action` (create/approve/reject), `outcome` | [StockRequestController.cs](../../Backend/PharmaGo.PharmacyService/Controllers/StockRequestController.cs) |
| **`pharmago_http_client_retries_total`** (nueva) | Counter | `target` | Polly, `onRetry` ([ResilienceMetrics.cs](../../Backend/Instrumentation/ResilienceMetrics.cs)) |
| **`pharmago_circuit_breaker_opened_total`** (nueva) | Counter | `target` | Polly, `onBreak` |
| **`pharmago_circuit_breaker_state`** (nueva) | Gauge | `target` (0 cerrado · 0.5 semiabierto · 1 abierto) | Polly, `onBreak` / `onHalfOpen` / `onReset` |
| **`process_runtime_dotnet_*`** (nuevas) | varias | `generation`, etc. | `OpenTelemetry.Instrumentation.Runtime`: GC, heap, thread pool, excepciones, locks, JIT |
| `login_invocations_total`, `active_user_count` | Counter / Gauge | — | Existían antes; `active_user_count` nunca se actualiza |

La lógica está en [CustomMetrics.cs](../../Backend/Instrumentation/CustomMetrics.cs) y la interfaz en [ICustomMetrics.cs](../../Backend/InstrumentationInterface/ICustomMetrics.cs).

**Camino hasta Prometheus.** Cada servicio exporta por OTLP/gRPC al OTel Collector (`.AddOtlpExporter(...)`). El collector las re-expone en formato Prometheus en `:8889`, y Prometheus las scrapea con el job `otlp-collector`. El gateway no exportaba métricas por OTLP; ahora sí.

**Dashboards y alertas usan solo esta vía (`job="otlp-collector"`)** por dos motivos:
- Cada réplica exporta con su propio `service.instance.id`, que en Prometheus aparece como `exported_instance`. El servicio aparece como `exported_job`.
- Cada servicio también expone `/metrics` y Prometheus lo scrapea por el Service (jobs `api-gateway`, `users-service`, `pharmacy-service`). Con 2 réplicas, cada scrape cae en un pod distinto. Además, ese exporter in-process le pone otros nombres a las series (`pharmago_http_requests_total_1`, `..._milliseconds_ms_bucket`). Ese scrape directo se mantiene solo para `up` (alerta "Servicio sin réplicas disponibles").

**Detalles de nombres:**
- Las requests de las probes del kubelet y del propio Prometheus (`/health`, `/metrics`) se excluyen con `endpoint!~"/health|/metrics"`.
- Los instrumentos con unidad `"1"` reciben el sufijo `_ratio` en el collector. Por eso el gauge del circuit breaker y el histograma de montos usan unidades-anotación (`{state}`, `{currency}`), que no se agregan al nombre.

**Dashboards de Grafana.** Se cargan por provisioning: datasource en [grafana-provisioning.yaml](../../k8s/configmaps/grafana-provisioning.yaml); el JSON de cada dashboard está indentado dentro de su ConfigMap.

| Dashboard | ConfigMap | Contenido |
|---|---|---|
| **PharmaGo - Infra** | [grafana-dashboard-infra.yaml](../../k8s/configmaps/grafana-dashboard-infra.yaml) | Nodo y pods (§3) |
| **PharmaGo - Overview** | [grafana-dashboards.yaml](../../k8s/configmaps/grafana-dashboards.yaml) | Los 8 paneles HTTP originales, con filtro OTLP y sin probes, más latencia p95 por servicio |
| **PharmaGo - Negocio y Runtime** (nuevo) | [grafana-dashboard-app.yaml](../../k8s/configmaps/grafana-dashboard-app.yaml) | Negocio (logins, % fallidos, compras, monto promedio, stock requests); saturación y resiliencia (requests en curso, circuit breaker, reintentos); runtime .NET (heap, GC, excepciones, thread pool, memoria comprometida, locks) |

Al revisar el Overview aparecieron dos queries mal escritas, que ahora están corregidas:
- *Distribución de Status Codes*: dividía un vector con labels por otro sin labels, y eso no devuelve nada.
- *Tasa de Error por Tipo*: le faltaba `group_left`.

Acceso a Grafana: [tutoriales/como-usar-grafana.md](../como-usar-grafana.md). Scraping en detalle: [tutoriales/como-funciona-prometheus.md](../como-funciona-prometheus.md).

## 5. Alertas de detección de anomalías (Grafana)

> Requisito: *"Se deben configurar, por lo menos, 5 alertas en Grafana para detección de anomalías de las métricas ya definidas."*

Hay **10 reglas**, provisionadas por archivo en [grafana-alerting.yaml](../../k8s/configmaps/grafana-alerting.yaml). Están en el grupo `pharmago-anomalias`, carpeta *PharmaGo*, y se evalúan cada 1 minuto. Se ven en **Grafana → Alerting → Alert rules**.

| # | Alerta | Detección | Dispara si | Severidad |
|---|---|---|---|---|
| 1 | Tasa de errores 5xx anómala | 5xx / total por servicio (5m) | > 5% durante 2m | critical |
| 2 | Latencia anómala respecto de la línea base | latencia media 5m ÷ latencia media 1h, por servicio | > 3× (y > 200 ms) durante 3m | warning |
| 3 | Tráfico anómalo en el gateway (z-score) | `abs(rate5m − media 1h) / desvío 1h` del tráfico que entra al gateway | z > 3 (y > 0.2 req/s) durante 2m | warning |
| 4 | Logins fallidos anómalos (posible fuerza bruta) | `pharmago_logins_total{outcome="failed"}` por minuto | > 5/min durante 1m | warning |
| 5 | CPU de pod cerca del límite | uso de CPU ÷ `limits.cpu` por pod | > 80% durante 3m | warning |
| 6 | Memoria de pod cerca del límite (riesgo de OOMKill) | working set ÷ `limits.memory` por pod | > 85% durante 3m | critical |
| 7 | Disco del nodo casi lleno | espacio libre por filesystem del nodo | < 15% durante 5m | critical |
| 8 | Circuit breaker abierto | `pharmago_circuit_breaker_state` | = 1 (inmediato) | critical |
| 9 | Saturación de requests en curso | `pharmago_http_requests_in_flight` por réplica | > 20 durante 2m | warning |
| 10 | Servicio sin réplicas disponibles | `up` del scrape directo del Service | = 0 durante 1m | critical |

Las reglas 2 y 3 comparan cada servicio contra **su propio comportamiento reciente** (línea base de 1 h): la 2 con un cociente, la 3 con un z-score. Las demás usan umbrales fijos sobre métricas de saturación. En todas, `noDataState: OK`: si una métrica todavía no existe (por ejemplo, no hubo ningún login fallido), la regla queda en Normal en lugar de disparar.

### Notificación a Discord

La regla notifica al contact point `discord-pharmago`. La URL del webhook **no está en el repo**:

1. En Discord: *Ajustes del canal → Integraciones → Webhooks → Nuevo webhook → Copiar URL*.
2. Antes de desplegar:
   ```bash
   export ALERT_WEBHOOK_URL="https://discord.com/api/webhooks/..."      # bash
   $env:ALERT_WEBHOOK_URL = "https://discord.com/api/webhooks/..."     # PowerShell
   ./apply-k8s.sh      # o .\apply-k8s.ps1
   ```
   El script crea el Secret `grafana-alerting`, y Grafana lo lee como variable de entorno (`$ALERT_WEBHOOK_URL` en el contact point).
3. Sin la variable, el script usa un placeholder: las alertas se evalúan y se ven en Grafana igual, pero no se envían.

Para cambiar el webhook sin redesplegar todo:
```bash
kubectl create secret generic grafana-alerting -n pharmago --from-literal=webhook_url="$ALERT_WEBHOOK_URL" --dry-run=client -o yaml | kubectl apply -f -
kubectl rollout restart deployment/grafana -n pharmago
```

### Cómo provocar cada alerta

| Alerta | Cómo provocarla |
|---|---|
| 1 y 10 | `kubectl scale deployment pharmago-pharmacy-service -n pharmago --replicas=0` y pedir `/api/drug` al gateway (responde 503). Volver con `--replicas=2`. |
| 3 | Tráfico sostenido contra el gateway (p. ej. 1 req/s durante varios minutos) después de un rato de calma. |
| 4 | Logins con contraseña incorrecta, uno cada ~5 s durante 3 min. |
| 5 | `chaos-engineering/cpu-spike.sh pharmago-api-gateway 300` |
| 6 | `kubectl exec -n pharmago <pod-users> -- sh -c "timeout 330 sh -c 'dd if=/dev/zero bs=280M count=1 \| sleep 330'"` (retiene ~280 MB; con el límite de 384Mi pasa del 85%). `ram-spike.sh` no alcanza, ver §7. |
| 2, 7, 8, 9 | Requieren degradación real (base de datos lenta, disco lleno, falla entre servicios, carga muy alta). Para la demo se puede bajar el umbral temporalmente desde la UI. |

### Evidencia (2026-11-07, Minikube)

Se provocaron las anomalías con los escenarios de la tabla anterior y se registró el estado de cada regla consultando la API de Grafana (`/api/prometheus/grafana/api/v1/rules`) cada 20 s. Horas en UTC−3.

| Regla | Anomalía provocada | Pending | Firing | Instancia que disparó |
|---|---|---|---|---|
| 10 Servicio sin réplicas | `pharmacy-service` a 0 réplicas (15:39:44 → 15:42:44) | 15:41:15 | 15:41:55 | `pharmacy-service`; vuelve a Normal al restaurarse |
| 1 Errores 5xx | 503 del gateway mientras no había backend | 15:42:15 | 15:43:56 | `PharmaGo.ApiGateway` |
| 3 Tráfico (z-score) | ~1 req/s sostenido después de un período de calma | 15:40:54 | 15:42:56 | gateway; vuelve a Normal cuando el tráfico pasa a ser la nueva línea base |
| 4 Logins fallidos | 12 logins con contraseña incorrecta por minuto | 15:41:55 | 15:42:56 | — (regla agregada) |
| 5 CPU de pod | `cpu-spike.sh pharmago-api-gateway 300` | 15:42:56 | 15:45:57 | **solo** `pharmago-api-gateway-…-cd25w`, el pod con el spike |
| 6 Memoria de pod | `dd bs=280M \| sleep` en un pod de users-service (92% del límite) | 15:48:05 | 15:51:10 | **solo** `pharmago-users-service-…-965vt`, sin OOMKill (0 reinicios) |

Las reglas 2, 7, 8 y 9 se evaluaron sin errores (`health: ok`) durante toda la prueba y se mantuvieron en Normal, lo esperable sin esa degradación. El **envío a Discord no se probó**: no había webhook configurado en el cluster de prueba, se usó el placeholder.

## 6. Logs en Kibana + Elasticsearch

```
Pods (stdout, 1 línea JSON por log) → Fluent Bit (DaemonSet) → Elasticsearch (pharmago-logs-*) → Kibana
```

Fluent Bit ([fluent-bit-config.yaml](../../k8s/configmaps/fluent-bit-config.yaml)) lee `/var/log/containers/*.log` de **todos** los pods del cluster, no solo los de PharmaGo — por eso el índice mezcla namespaces (`pharmago`, `kube-system`, y lo que haya corrido antes en `default`). Para quedarse solo con los logs de la app:

```
kubernetes.namespace_name : "pharmago"
```

Un filtro JSON (`Key_Name log`, `Parser dotnet_console_json`) toma esa línea JSON y **reemplaza** el campo `log` por sus propios campos a nivel raíz del documento (`Timestamp`, `LogLevel`, `Category`, `Message`, `State`, `Scopes`). Esto es importante: **el campo `log` no existe** para estos documentos (solo para logs que no son JSON, como los de SQL Server) — cualquier búsqueda vieja tipo `log:*logged*` no encuentra nada.

**Verificado contra el cluster (2026-11-06-07):** hice login correcto e incorrecto por el gateway y confirmé el documento resultante en Elasticsearch:

```json
{
  "Category": "Instrumentation.StructuredLogger",
  "Message": "Login succeeded pharma_biz=login_success EVTLGOK correlation_id=1ba945ee-...",
  "Scopes": [
    ...,
    { "correlation_id": "1ba945ee-...", "service": "PharmaGo.UsersService",
      "pharma_biz": "login_success", "component": "LoginController",
      "operation": "login", "outcome": "success", "user_name": "admin", "user_id": 1 }
  ]
}
```

Y que el mismo `correlation_id` aparece también en el log del API Gateway para esa misma request — la correlación entre servicios funciona de punta a punta.

Guía completa de consultas en Kibana (campos reales, ejemplos probados, cómo seguir una request con `correlation_id`): [tutoriales/como-revisar-logs-kibana.md](../como-revisar-logs-kibana.md). Acceso rápido:

```bash
cd k8s && ./port-forward.sh     # Kibana queda en http://127.0.0.1:5601
```

Ya existe el Data View `pharmago-logs-*` creado y puesto como default en Kibana (no hay que crearlo a mano).

## 7. Limitaciones conocidas

1. **Trazas sin backend de visualización** (§2). Se emiten correctamente en OTLP y el collector las recibe, pero el único exporter configurado es `debug` (stdout del collector). No cumple el espíritu de "poder ver una traza", solo el de "emitirla en el protocolo correcto". Sugerencia: agregar Grafana Tempo (mismo namespace, mismo Grafana) como backend y un exporter OTLP en el collector apuntando a él.
2. **Red por pod** (§3). En Minikube (cri-dockerd + cgroup v2) el cAdvisor del kubelet no publica `container_network_*` ni `container_fs_usage_bytes` por pod. La red está en el dashboard a nivel nodo, y el storage por pod se mide como I/O de disco.

   El manifiesto [cadvisor-daemonset.yaml](../../k8s/deployments/ops/cadvisor-daemonset.yaml) despliega un cAdvisor standalone que sí las publica. Para activarlo hacen falta tres pasos, y requiere aprobar un DaemonSet `privileged` con montajes del host:
   - aplicarlo;
   - agregar a Prometheus un job `cadvisor`;
   - regenerar el dashboard de infra con la fila "Red y filesystem por pod".
3. **Métricas de resiliencia sin tráfico** (§4). Los clientes HTTP `UsersServiceClient` y `PharmacyServiceClient`, envueltos por Polly, no se usan en ningún controller ni manager. Las series `pharmago_circuit_breaker_state` existen (en 0, cerrado), pero reintentos y aperturas no tienen datos y la alerta 8 no puede dispararse hasta que haya llamadas reales entre servicios.
4. **`ram-spike.sh` no alcanza para probar la alerta de memoria.** Escribe en `/dev/shm`, que en Kubernetes está limitado a 64 MiB; si falla, pasa a `/tmp`, que es disco. Para la prueba se usó `dd if=/dev/zero bs=280M count=1 | sleep 300` dentro del pod: `dd` retiene un buffer de 280 MB.
5. **El stack legado de Docker Compose** (`docker-compose.yml`, `grafana/provisioning/`) no tiene los dashboards ni las alertas nuevas. Solo se actualizó el despliegue de Kubernetes.
6. **Ruido de `kube-system` en los logs** (§6). Fluent Bit no filtra por namespace antes de enviar a Elasticsearch; hay que filtrar en Kibana (`kubernetes.namespace_name : "pharmago"`). No es un problema funcional, pero infla el índice con logs irrelevantes para la app.
7. **El tutorial de logging estructurado está desactualizado** (§1): [como-agregar-log-estructurado.md](../logging/como-agregar-log-estructurado.md) muestra un ejemplo con campos `status`/`message` que ya no coincide con el código real de `LoginController.cs` (`pharma_biz`/`component`/`operation`/`outcome`).
