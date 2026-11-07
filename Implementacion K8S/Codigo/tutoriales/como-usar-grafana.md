# Configuración de Grafana - PharmaGo

## ☸️ En Kubernetes (despliegue actual)

```bash
cd k8s && ./port-forward.sh     # Grafana en http://127.0.0.1:3000  (admin / admin)
```

Todo se carga por provisioning al arrancar el pod, sin pasos manuales:

| Qué | Dónde se define |
|---|---|
| Datasource Prometheus | `k8s/configmaps/grafana-provisioning.yaml` |
| Dashboard **PharmaGo - Infra** (nodo y pods: CPU, memoria, storage, red) | `k8s/configmaps/grafana-dashboard-infra.yaml` |
| Dashboard **PharmaGo - Overview** (HTTP: tráfico, latencia, errores, 429) | `k8s/configmaps/grafana-dashboards.yaml` |
| Dashboard **PharmaGo - Negocio y Runtime** (logins, compras, stock requests, requests en curso, circuit breaker, runtime .NET) | `k8s/configmaps/grafana-dashboard-app.yaml` |
| **10 alertas** de anomalías y contact point Discord | `k8s/configmaps/grafana-alerting.yaml` |

- **Alertas**: menú ☰ → **Alerting → Alert rules**, carpeta *PharmaGo*. Cada regla pasa por *Normal → Pending → Firing*. Las notificaciones van al webhook de Discord configurado con `ALERT_WEBHOOK_URL`.
- **Modificar un dashboard**: editar el JSON dentro del ConfigMap, `kubectl apply -f` y `kubectl rollout restart deployment/grafana -n pharmago`. El ConfigMap se monta con `subPath`, que no se recarga solo. Los cambios hechos desde la UI se pierden al reiniciar el pod.
- Detalle de cada métrica, las queries y la lista de alertas: [observabilidad/README.md](observabilidad/README.md).

La sección siguiente describe el stack legado de **Docker Compose**, que no incluye los dashboards ni las alertas nuevas.

## 🎯 Configuración Automática

Grafana se configura automáticamente al levantar Docker Compose. No necesitas hacer nada manualmente.

### ✅ Qué se Configura Automáticamente

1. **Datasource de Prometheus**
   - URL: `http://prometheus:9090`
   - Configurado como datasource por defecto
   - Listo para usar

2. **Dashboard de Ejemplo**
   - "PharmaGo - Overview"
   - 4 paneles pre-configurados
   - Se carga automáticamente

3. **Persistencia**
   - Dashboards personalizados se guardan
   - Configuración persiste entre reinicios

## 🚀 Acceso a Grafana

```bash
# URL
http://localhost:3000

# Credenciales
Usuario: admin
Password: admin
```

## 📊 Dashboard Pre-configurado

### PharmaGo - Overview

**Paneles incluidos:**

1. **Peticiones por Segundo por Endpoint**
   - Muestra el tráfico de cada endpoint
   - Actualización cada 5 segundos

2. **Latencia Promedio por Endpoint**
   - Tiempo de respuesta en milisegundos
   - Muestra media y máximo

3. **Tasa de Error por Endpoint**
   - Porcentaje de errores
   - Alerta visual si supera 5%

4. **Throughput Total**
   - Peticiones totales por segundo
   - Gauge con umbrales de color

## 📁 Estructura de Archivos

```
grafana/
└── provisioning/
    ├── datasources/
    │   └── prometheus.yml          # Configuración de Prometheus
    └── dashboards/
        ├── dashboards.yml          # Configuración de dashboards
        └── pharmago-overview.json  # Dashboard de ejemplo
```

## 🔧 Configuración del Datasource

**Archivo:** `grafana/provisioning/datasources/prometheus.yml`

```yaml
apiVersion: 1

datasources:
  - name: Prometheus
    type: prometheus
    access: proxy
    url: http://prometheus:9090
    isDefault: true
    editable: true
    jsonData:
      timeInterval: 5s
      queryTimeout: 60s
      httpMethod: POST
```

**Parámetros:**
- `url`: Dirección interna de Prometheus en Docker
- `isDefault`: Se usa por defecto en todos los dashboards
- `editable`: Permite modificar la configuración desde la UI
- `timeInterval`: Intervalo mínimo de scraping (5 segundos)

## 📈 Crear Dashboards Personalizados

### Opción 1: Desde la UI

1. Accede a Grafana: `http://localhost:3000`
2. Click en "+" → "Dashboard"
3. Click en "Add new panel"
4. Escribe tu query de Prometheus
5. Guarda el dashboard

**Ejemplo de query:**

```promql
# Peticiones por endpoint
sum by (endpoint) (rate(pharmago_http_requests_total[5m]))

# Latencia P95
histogram_quantile(0.95, rate(pharmago_http_request_duration_milliseconds_bucket[5m]))

# Tasa de error
sum(rate(pharmago_http_errors_total[5m])) / sum(rate(pharmago_http_requests_total[5m])) * 100
```

### Opción 2: Exportar/Importar Dashboards

**Exportar un dashboard:**

1. Abre el dashboard
2. Click en el ícono de configuración (⚙️)
3. Click en "JSON Model"
4. Copia el JSON
5. Guarda en `grafana/provisioning/dashboards/mi-dashboard.json`

**Importar un dashboard:**

1. Coloca el archivo JSON en `grafana/provisioning/dashboards/`
2. Reinicia Grafana: `docker-compose restart grafana`
3. El dashboard aparecerá automáticamente

### Opción 3: Importar desde Grafana.com

1. Ve a [grafana.com/grafana/dashboards](https://grafana.com/grafana/dashboards)
2. Busca dashboards para Prometheus
3. Copia el ID del dashboard
4. En Grafana: "+" → "Import" → Pega el ID

**Dashboards recomendados:**
- **1860**: Node Exporter Full
- **3662**: Prometheus 2.0 Overview
- **7362**: Prometheus Stats

## 🎨 Queries Útiles para Dashboards

### Métricas de Peticiones

```promql
# Total de peticiones
sum(pharmago_http_requests_total)

# Peticiones por segundo
sum(rate(pharmago_http_requests_total[5m]))

# Top 10 endpoints más usados
topk(10, sum by (endpoint) (rate(pharmago_http_requests_total[5m])))

# Distribución por método HTTP
sum by (method) (rate(pharmago_http_requests_total[5m]))

# Distribución por status code
sum by (status_code) (rate(pharmago_http_requests_total[5m]))
```

### Métricas de Latencia

```promql
# Latencia promedio
rate(pharmago_http_request_duration_milliseconds_sum[5m]) / rate(pharmago_http_request_duration_milliseconds_count[5m])

# Latencia P50, P95, P99
histogram_quantile(0.50, rate(pharmago_http_request_duration_milliseconds_bucket[5m]))
histogram_quantile(0.95, rate(pharmago_http_request_duration_milliseconds_bucket[5m]))
histogram_quantile(0.99, rate(pharmago_http_request_duration_milliseconds_bucket[5m]))

# Top 10 endpoints más lentos
topk(10, rate(pharmago_http_request_duration_milliseconds_sum[5m]) / rate(pharmago_http_request_duration_milliseconds_count[5m]))
```

### Métricas de Errores

```promql
# Tasa de error total
sum(rate(pharmago_http_errors_total[5m])) / sum(rate(pharmago_http_requests_total[5m])) * 100

# Errores por segundo
sum(rate(pharmago_http_errors_total[5m]))

# Errores por tipo
sum by (error_type) (rate(pharmago_http_errors_total[5m]))

# Endpoints con más errores
topk(5, sum by (endpoint) (rate(pharmago_http_errors_total[5m])))
```

### Métricas de Disponibilidad

```promql
# Availability (% de peticiones exitosas)
sum(rate(pharmago_http_requests_total{status_code=~"2.."}[5m])) / sum(rate(pharmago_http_requests_total[5m])) * 100

# Peticiones activas
http_server_active_requests

# Uptime de servicios
up{job=~"users-service|pharmacy-service|api-gateway"}
```

## 🚨 Configurar Alertas

### Crear una Alerta en Grafana

1. Abre un panel
2. Click en "Alert" tab
3. Click en "Create alert rule from this panel"
4. Configura la condición
5. Configura notificaciones

**Ejemplo de alerta:**

```yaml
Nombre: Alta Tasa de Error
Condición: Tasa de error > 5%
Query: sum(rate(pharmago_http_errors_total[5m])) / sum(rate(pharmago_http_requests_total[5m])) * 100
Threshold: 5
Duración: 5 minutos
```

### Canales de Notificación

**Email:**
1. "Alerting" → "Contact points"
2. "New contact point"
3. Tipo: Email
4. Configurar SMTP

**Slack:**
1. "Alerting" → "Contact points"
2. "New contact point"
3. Tipo: Slack
4. Webhook URL de Slack

**Discord:**
1. "Alerting" → "Contact points"
2. "New contact point"
3. Tipo: Discord
4. Webhook URL de Discord

## 🎯 Dashboards Recomendados

### Dashboard 1: Service Overview

**Paneles:**
- Peticiones por segundo (por servicio)
- Latencia promedio (por servicio)
- Tasa de error (por servicio)
- Peticiones activas

### Dashboard 2: Endpoint Analysis

**Paneles:**
- Top 10 endpoints por tráfico
- Top 10 endpoints por latencia
- Top 10 endpoints con más errores
- Distribución de status codes

### Dashboard 3: SLIs/SLOs

**Paneles:**
- Availability (objetivo: 99.9%)
- Latency P95 (objetivo: < 500ms)
- Error Rate (objetivo: < 1%)
- Throughput

### Dashboard 4: Business Metrics

**Paneles:**
- Logins por hora
- Usuarios activos
- Operaciones de farmacia
- Compras realizadas

## 🔍 Troubleshooting

### Problema: Datasource no aparece

**Solución:**
```bash
# Verificar que el archivo existe
ls grafana/provisioning/datasources/prometheus.yml

# Verificar logs de Grafana
docker logs grafana

# Reiniciar Grafana
docker-compose restart grafana
```

### Problema: Dashboard no se carga

**Solución:**
```bash
# Verificar que el JSON es válido
cat grafana/provisioning/dashboards/pharmago-overview.json | jq .

# Verificar logs
docker logs grafana | grep dashboard

# Reiniciar Grafana
docker-compose restart grafana
```

### Problema: No hay datos en los paneles

**Solución:**
```bash
# Verificar que Prometheus está funcionando
curl http://localhost:9090/api/v1/query?query=up

# Verificar que hay métricas
curl http://localhost:9090/api/v1/query?query=pharmago_http_requests_total

# Generar tráfico
for i in {1..50}; do curl http://localhost:5000/api/drug; done
```

### Problema: Grafana no se conecta a Prometheus

**Solución:**
```bash
# Verificar que ambos están en la misma red
docker network inspect codigo_pharmago-network

# Verificar conectividad desde Grafana
docker exec grafana ping prometheus

# Verificar URL del datasource
# Debe ser: http://prometheus:9090 (no localhost)
```

## 📚 Recursos Adicionales

### Documentación Oficial

- [Grafana Documentation](https://grafana.com/docs/grafana/latest/)
- [Prometheus Queries](https://prometheus.io/docs/prometheus/latest/querying/basics/)
- [Dashboard Best Practices](https://grafana.com/docs/grafana/latest/best-practices/best-practices-for-creating-dashboards/)

### Ejemplos de Queries

Ver `Backend/METRICS_GUIDE.md` para más queries de ejemplo.

### Dashboards de la Comunidad

- [Grafana Dashboards](https://grafana.com/grafana/dashboards/)
- [Awesome Prometheus Alerts](https://awesome-prometheus-alerts.grep.to/)

## ✅ Checklist de Configuración

- [x] Datasource de Prometheus configurado automáticamente
- [x] Dashboard de ejemplo incluido
- [x] Persistencia de datos configurada
- [x] Provisioning automático funcionando
- [ ] Crear dashboards personalizados según necesidades
- [ ] Configurar alertas para métricas críticas
- [ ] Configurar canales de notificación

## 🎯 Resumen

### Configuración Automática

✅ Datasource: `http://prometheus:9090`  
✅ Dashboard: "PharmaGo - Overview"  
✅ Persistencia: Volumen `grafana_data`  
✅ Provisioning: Configurado en `grafana/provisioning/`  

### Acceso

🌐 URL: `http://localhost:3000`  
👤 Usuario: `admin`  
🔑 Password: `admin`  

### Próximos Pasos

1. Accede a Grafana
2. Explora el dashboard pre-configurado
3. Crea dashboards personalizados
4. Configura alertas según tus necesidades

