# Configuración de Prometheus para Microservicios

## Resumen

Todos los servicios de PharmaGo están ahora conectados a Prometheus para monitoreo y observabilidad.

## Arquitectura de Métricas

```
┌─────────────────────────────────────────────────────────────┐
│                      Prometheus :9090                        │
│                    (Scraping Metrics)                        │
└──────────────────────┬──────────────────────────────────────┘
                       │
       ┌───────────────┼───────────────┬──────────────────┐
       │               │               │                  │
       ▼               ▼               ▼                  ▼
┌─────────────┐ ┌─────────────┐ ┌─────────────┐ ┌─────────────┐
│ API Gateway │ │Users Service│ │Pharmacy Svc │ │OTLP Collect.│
│   :80       │ │   :80       │ │   :80       │ │   :8889     │
│ /metrics    │ │ /metrics    │ │ /metrics    │ │ /metrics    │
└─────────────┘ └─────────────┘ └─────────────┘ └─────────────┘
```

## Servicios Monitoreados

### 1. API Gateway (pharmago-api-gateway:80)
- **Job Name**: `api-gateway`
- **Endpoint**: `http://pharmago-api-gateway:80/metrics`
- **Métricas**:
  - Solicitudes HTTP entrantes
  - Latencia de proxy
  - Errores de enrutamiento
  - Métricas de ASP.NET Core

### 2. Users Service (pharmago-users-service:80)
- **Job Name**: `users-service`
- **Endpoint**: `http://pharmago-users-service:80/metrics`
- **Métricas**:
  - Solicitudes HTTP
  - Operaciones de login
  - Creación de usuarios
  - Gestión de invitaciones
  - Métricas personalizadas (LoginInvocations)
  - Llamadas HTTP a Pharmacy Service

### 3. Pharmacy Service (pharmago-pharmacy-service:80)
- **Job Name**: `pharmacy-service`
- **Endpoint**: `http://pharmago-pharmacy-service:80/metrics`
- **Métricas**:
  - Solicitudes HTTP
  - Operaciones de medicamentos
  - Gestión de stock
  - Compras
  - Llamadas HTTP a Users Service

### 4. OTLP Collector (otlp-collector:8889)
- **Job Name**: `otlp-collector`
- **Endpoint**: `http://otlp-collector:8889/metrics`
- **Métricas**:
  - Estado del collector
  - Métricas agregadas de todos los servicios

## Configuración de Prometheus

El archivo `prometheus.yml` está configurado con:

```yaml
global:
  scrape_interval: 5s  # Scraping cada 5 segundos

scrape_configs:
  - job_name: 'otlp-collector'
    metrics_path: /metrics
    static_configs:
      - targets: ['otlp-collector:8889']
  
  - job_name: 'users-service'
    metrics_path: /metrics
    static_configs:
      - targets: ['pharmago-users-service:80']
  
  - job_name: 'pharmacy-service'
    metrics_path: /metrics
    static_configs:
      - targets: ['pharmago-pharmacy-service:80']
  
  - job_name: 'api-gateway'
    metrics_path: /metrics
    static_configs:
      - targets: ['pharmago-api-gateway:80']
```

## OpenTelemetry Configuration

Cada servicio está configurado con:

### Exportadores
1. **Prometheus Exporter**: Expone métricas en formato Prometheus en `/metrics`
2. **OTLP Exporter**: Envía métricas al OpenTelemetry Collector

### Instrumentación
- **ASP.NET Core Instrumentation**: Métricas de HTTP requests, responses, latencia
- **HTTP Client Instrumentation**: Métricas de llamadas HTTP salientes entre servicios
- **Custom Metrics**: Métricas personalizadas del negocio (ej: LoginInvocations)

## Acceso a Métricas

### Prometheus UI
- **URL**: http://localhost:9090
- **Queries de ejemplo**:
  ```promql
  # Tasa de requests por servicio
  rate(http_server_request_duration_seconds_count[1m])
  
  # Latencia P95 del API Gateway
  histogram_quantile(0.95, rate(http_server_request_duration_seconds_bucket{job="api-gateway"}[5m]))
  
  # Total de logins
  pharmago_login_invocations_total
  
  # Requests por endpoint
  http_server_request_duration_seconds_count{job="users-service"}
  ```

### Grafana
- **URL**: http://localhost:3000
- **Credenciales**: admin / admin
- **Data Source**: Prometheus (http://prometheus:9090)

### Métricas Directas (para debugging)
- API Gateway: http://localhost:5000/metrics
- Users Service: http://localhost:5001/metrics
- Pharmacy Service: http://localhost:5002/metrics

## Métricas Disponibles

### Métricas Estándar de ASP.NET Core

```
# Duración de requests HTTP
http_server_request_duration_seconds_bucket
http_server_request_duration_seconds_sum
http_server_request_duration_seconds_count

# Requests activos
http_server_active_requests

# Tamaño de responses
http_server_response_body_size_bytes
```

### Métricas de HTTP Client (Comunicación entre servicios)

```
# Duración de llamadas HTTP salientes
http_client_request_duration_seconds_bucket
http_client_request_duration_seconds_sum
http_client_request_duration_seconds_count

# Requests activos salientes
http_client_active_requests
```

### Métricas Personalizadas

```
# Login invocations (Users Service)
pharmago_login_invocations_total
```

## Dashboards Recomendados para Grafana

### 1. Dashboard de Overview
- Total de requests por servicio
- Tasa de errores
- Latencia P50, P95, P99
- Requests activos

### 2. Dashboard de Users Service
- Logins por minuto
- Creación de usuarios
- Gestión de invitaciones
- Errores de autenticación

### 3. Dashboard de Pharmacy Service
- Operaciones de medicamentos
- Gestión de stock
- Compras procesadas
- Exportaciones

### 4. Dashboard de API Gateway
- Distribución de tráfico por servicio
- Latencia de proxy
- Errores de enrutamiento

### 5. Dashboard de Comunicación entre Servicios
- Llamadas Users → Pharmacy
- Llamadas Pharmacy → Users
- Latencia de comunicación inter-servicio
- Errores de comunicación

## Alertas Recomendadas

```yaml
# Alta tasa de errores
- alert: HighErrorRate
  expr: rate(http_server_request_duration_seconds_count{status=~"5.."}[5m]) > 0.05
  for: 5m
  annotations:
    summary: "Alta tasa de errores en {{ $labels.job }}"

# Alta latencia
- alert: HighLatency
  expr: histogram_quantile(0.95, rate(http_server_request_duration_seconds_bucket[5m])) > 1
  for: 5m
  annotations:
    summary: "Alta latencia en {{ $labels.job }}"

# Servicio caído
- alert: ServiceDown
  expr: up{job=~".*-service|api-gateway"} == 0
  for: 1m
  annotations:
    summary: "Servicio {{ $labels.job }} no responde"
```

## Verificación

Para verificar que todos los servicios están conectados:

1. **Accede a Prometheus**: http://localhost:9090
2. **Ve a Status → Targets**: http://localhost:9090/targets
3. **Verifica que todos los targets estén UP**:
   - otlp-collector (1/1 up)
   - users-service (1/1 up)
   - pharmacy-service (1/1 up)
   - api-gateway (1/1 up)

## Troubleshooting

### Target muestra "DOWN"
1. Verifica que el servicio esté ejecutándose:
   ```bash
   docker ps | grep pharmago
   ```

2. Verifica que el endpoint /metrics responda:
   ```bash
   curl http://localhost:5001/metrics  # Users Service
   curl http://localhost:5002/metrics  # Pharmacy Service
   curl http://localhost:5000/metrics  # API Gateway
   ```

3. Revisa los logs del servicio:
   ```bash
   docker logs pharmago-users-service
   docker logs pharmago-pharmacy-service
   docker logs pharmago-api-gateway
   ```

### No se ven métricas personalizadas
1. Verifica que el código esté llamando a `_customMetrics.LoginInvocations()`
2. Verifica que el servicio tenga configurado `AddMeter("PharmaGo.CustomMetrics")`
3. Reinicia el servicio después de cambios

### Prometheus no hace scraping
1. Verifica la configuración en `prometheus.yml`
2. Reinicia Prometheus:
   ```bash
   docker-compose restart prometheus
   ```
3. Verifica los logs de Prometheus:
   ```bash
   docker logs prometheus
   ```

## Conclusión

✅ Todos los servicios están conectados a Prometheus
✅ Métricas expuestas en formato Prometheus
✅ Doble exportación: Prometheus + OTLP Collector
✅ Instrumentación completa de HTTP y llamadas inter-servicio
✅ Listo para dashboards y alertas en Grafana

