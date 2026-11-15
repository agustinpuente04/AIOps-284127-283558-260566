# Guía de Logging con Elasticsearch + Kibana + Logstash - PharmaGo

## 📋 Configuración Implementada

### Stack de Logging Completo

**3 componentes:**
1. **Elasticsearch**: Almacenamiento y búsqueda de logs (puerto 9200)
2. **Kibana**: Visualización y consulta de logs (puerto 5601)
3. **Logstash**: Recopilación y procesamiento de logs (puerto 5044, 9600)

### Arquitectura

**Docker Compose:**
```
Contenedores Docker (stdout/stderr) → Archivos JSON → Logstash → Elasticsearch → Kibana
```

**Kubernetes (K8s):**
```
Pods (stdout/stderr) → Fluent Bit (DaemonSet) → Elasticsearch → Kibana
```
En K8s, **Fluent Bit** recolecta los logs de los pods y los envía a Elasticsearch con índice `pharmago-logs-*`.

## 🚀 Estado Actual

### Servicios Funcionando

- ✅ **Elasticsearch**: http://127.0.0.1:9200
- ✅ **Kibana**: http://127.0.0.1:5601
- ✅ **Logstash**: Recopilando logs de contenedores Docker

### Verificar que Funciona

1. **Verificar que hay índices en Elasticsearch** (antes de crear el data view):
   ```powershell
   Invoke-WebRequest -Uri "http://127.0.0.1:9200/_cat/indices?v" -UseBasicParsing
   ```
   Deberías ver índices `pharmago-logs-YYYY.MM.DD`. Si no hay ninguno, espera 2–3 minutos tras el despliegue para que Fluent Bit empiece a enviar logs (en K8s).

2. **Accede a Kibana**: http://127.0.0.1:5601
2. **Crea un Data View** (antes "Index Pattern" en Kibana 8+):
   - Ve a **Stack Management** → **Data Views** (o menú ☰ → Management → Data Views)
   - Click en **Create data view**
   - En **Name**: `pharmago-logs`
   - En **Index pattern**: `pharmago-logs-*`
   - En **Timestamp field**: `@timestamp`
   - Click en **Save data view to Kibana**

3. **Ver Logs en Discover**:
   - Ve a **Discover** en el menú lateral
   - En el selector superior, selecciona el data view `pharmago-logs`
   - Deberías ver logs de tus servicios

## 📊 Cómo Usar los Logs

### 1. Ver Logs en Kibana

1. Accede a Kibana: http://127.0.0.1:5601
2. Ve a **Discover**
3. Selecciona el data view `pharmago-logs`
4. Verás todos los logs de tus servicios

### 2. Filtrar Logs por Servicio

En la barra de búsqueda de Kibana:

```
kubernetes.labels.app: "pharmago-users-service"
```
o por namespace: `kubernetes.namespace_name: "pharmago"`

### 3. Estructura real de un documento (verificado 2026-11-06)

Los logs de los servicios .NET se emiten como una línea JSON (`Logging:Console:FormatterName: json` en cada `appsettings.json`). Fluent Bit la parsea (`dotnet_console_json` en `parsers.conf`) y **reemplaza** el campo `log` original por sus campos: en los documentos ya no existe un campo `log` de texto para los logs de la app (las búsquedas tipo `log:*logged*` de versiones anteriores de esta guía **no devuelven nada** con los logs estructurados actuales; solo seguían funcionando con líneas que no son JSON, como las de SQL).

Documento real de un login correcto (`PharmaGo.UsersService`, vía `IStructuredLogger`):

```json
{
  "@timestamp": "2026-11-07T00:49:49.808Z",
  "LogLevel": "Information",
  "Category": "Instrumentation.StructuredLogger",
  "Message": "Login succeeded pharma_biz=login_success EVTLGOK correlation_id=1ba945ee-...",
  "Scopes": [
    { "SpanId": "...", "TraceId": "...", "ParentId": "..." },
    { "ConnectionId": "..." },
    { "RequestId": "...", "RequestPath": "/api/login" },
    { "correlation_id": "1ba945ee-...", "service": "PharmaGo.UsersService" },
    {
      "timestamp_utc": "...", "level": "info", "service": "PharmaGo.UsersService",
      "correlation_id": "1ba945ee-...", "pharma_biz": "login_success",
      "component": "LoginController", "operation": "login", "outcome": "success",
      "user_name": "admin", "user_id": 1
    }
  ],
  "kubernetes": { "pod_name": "...", "labels": { "app": "pharmago-users-service" } }
}
```

Campos útiles para buscar/filtrar en Kibana:

| Campo | Qué es |
|---|---|
| `Message` | Mensaje legible (incluye `pharma_biz=...` y el token `EVT...` para búsqueda exacta) |
| `Category` | Clase que logueó: `Instrumentation.StructuredLogger` (eventos de negocio) o `Instrumentation.MetricsMiddleware` (una línea por cada request HTTP) |
| `Scopes.pharma_biz` | Tipo de evento de negocio: `login_success`, `login_fail`, `purchase_create`, `purchase_create_fail` |
| `Scopes.component` / `Scopes.operation` / `Scopes.outcome` | Quién generó el evento y si salió bien o mal |
| `Scopes.user_name` / `Scopes.user_id` | Usuario involucrado |
| `Scopes.correlation_id` | ID que viaja desde el API Gateway hasta el microservicio — une todos los logs de una misma request entre servicios |
| `kubernetes.labels.app` | Qué servicio/pod generó el log |
| `Exception` | Stack trace completo, solo en los `LogWarning`/`LogError` con excepción |

> Los campos de `Scopes.*` están mapeados como texto + `.keyword` (lo mismo que `Message` y `Category`), así que sirven para búsquedas exactas o con comodines.

### 4. Buscar Logs de Login

**KQL (por defecto en la barra de búsqueda de Discover):**

```
Scopes.pharma_biz : "login_success"
Scopes.pharma_biz : "login_fail"
Scopes.pharma_biz : "login_success" or Scopes.pharma_biz : "login_fail"
```

**Por usuario específico:**

```
Scopes.pharma_biz : "login_fail" and Scopes.user_name : "admin"
```

**Lucene** (si cambiás el selector KQL → Lucene en la barra), usando el token corto pensado para esto:

```
Message:EVTLGOK   # logins exitosos
Message:EVTLGFL   # logins fallidos
```

**Test rápido:** Llama al login y comprueba que el log aparece:
1. Desde la terminal (reemplazá `$URL` por la que te da `minikube service pharmago-ui -n pharmago --url`):
   ```bash
   curl -X POST "$URL/api/login" -H "Content-Type: application/json" \
     -d '{"userName":"admin","password":"Abcdef12."}'
   ```
   (usuario de prueba `admin` / `Abcdef12.` cargado por `Backend/seed-data.sql`)
2. En Kibana, abre **Discover**, data view `pharmago-logs` (ya debería estar seleccionado por defecto).
3. Busca `Scopes.pharma_biz : "login_success"` y ajusta el rango de tiempo arriba a la derecha (p. ej. "Last 15 minutes").
4. Deberías ver el documento con `Message: "Login succeeded pharma_biz=login_success EVTLGOK ..."`.

### 5. Seguir una request de punta a punta (correlation_id)

Abrí cualquier documento y copiá `Scopes.correlation_id` (el del último scope, el de `StructuredLogger`/`CorrelationIdMiddlewareExtensions`). Buscando ese valor exacto aparecen **todos** los logs de esa request, en el gateway y en el microservicio, en orden:

```
Scopes.correlation_id : "1ba945ee-55f0-4800-b500-d884ed27857a"
```

### 6. Búsquedas Avanzadas

```
# Errores (con o sin stack trace)
LogLevel: "Error" or LogLevel: "Warning"

# Solo eventos de negocio (ignora las líneas de MetricsMiddleware/EF/framework)
Category: "Instrumentation.StructuredLogger"

# Un servicio puntual
kubernetes.labels.app : "pharmago-pharmacy-service"

# Requests lentas del gateway/servicios (campo numérico, State.ElapsedMs)
State.ElapsedMs > 500
```

### 5. Verificar Logs en Elasticsearch

```powershell
# Verificar que hay logs indexados
Invoke-WebRequest -Uri "http://127.0.0.1:9200/pharmago-logs-*/_count" -UseBasicParsing

# Ver algunos logs
Invoke-WebRequest -Uri "http://127.0.0.1:9200/pharmago-logs-*/_search?size=5" -UseBasicParsing
```

## 🔧 Configuración Actual

### Recolección de logs

**Docker Compose:** Logstash lee archivos JSON de Docker y envía a Elasticsearch.

**K8s:** Fluent Bit (DaemonSet) lee logs de `/var/log/containers` y envía a Elasticsearch con índice `pharmago-logs-YYYY.MM.DD`.

### Elasticsearch

- **Puerto HTTP**: 9200
- **Puerto Transport**: 9300
- **Seguridad**: Deshabilitada (para desarrollo)
- **Memoria**: 512MB (configurable)

### Kibana

- **Puerto**: 5601
- **Conectado a**: Elasticsearch en http://elasticsearch:9200

## 📈 Crear Dashboards en Kibana

### Dashboard Básico de Logs

1. Ve a **Dashboard** → **Create Dashboard**
2. Agrega visualizaciones:
   - **Logs por servicio** (Pie chart)
   - **Logs en el tiempo** (Line chart)
   - **Top errores** (Data table)
   - **Logs recientes** (Logs viewer)

### Visualización: Logs por Servicio

1. **Visualize** → **Create Visualization** → **Pie**
2. Selecciona el data view `pharmago-logs`
3. Agrega un bucket: **Split slices**
4. Agrega términos por campo: `service.keyword`
5. Guarda la visualización

### Visualización: Logs en el Tiempo

1. **Visualize** → **Create Visualization** → **Line**
2. Selecciona el data view `pharmago-logs`
3. Agrega eje X: **Date Histogram** por `@timestamp`
4. Agrega eje Y: **Count**
5. Guarda la visualización

## 🔍 Troubleshooting

### Problema: Kibana dice "no match any data streams, indices..."

**Causa:** No hay índices en Elasticsearch que coincidan con `pharmago-logs-*`.

**Solución (K8s):**
1. Verifica índices existentes:
   ```powershell
   Invoke-WebRequest -Uri "http://127.0.0.1:9200/_cat/indices?v" -UseBasicParsing
   ```
2. Si no hay `pharmago-logs-*`, comprueba Fluent Bit:
   ```bash
   kubectl get pods -n pharmago -l k8s-app=fluent-bit-logging
   kubectl logs -n pharmago -l k8s-app=fluent-bit-logging --tail=50
   ```
3. Espera 2–3 minutos tras el despliegue para que lleguen los primeros logs.
4. Si usas Docker Compose, verifica Logstash: `docker ps | grep logstash`

### Problema: No veo logs en Kibana (data view creado pero vacío)

**Solución:**
1. Verifica que Elasticsearch tenga datos:
   ```powershell
   Invoke-WebRequest -Uri "http://127.0.0.1:9200/pharmago-logs-*/_count" -UseBasicParsing
   ```
2. Verifica que el data view esté creado en Kibana (Stack Management → Data Views)

### Problema: Logstash no recopila logs

**Solución:**
1. Verifica que los contenedores estén generando logs:
   ```bash
   docker logs pharmago-api-gateway --tail 10
   ```
2. Verifica que Logstash tenga acceso a los archivos:
   ```bash
   docker exec logstash ls -la /var/lib/docker/containers/ | Select-Object -First 5
   ```
3. Revisa los logs de Logstash: `docker logs logstash`

### Problema: Elasticsearch no responde

**Solución:**
1. Verifica que Elasticsearch esté corriendo: `docker ps | grep elasticsearch`
2. Verifica los logs: `docker logs elasticsearch`
3. Verifica el health: http://127.0.0.1:9200/_cluster/health

## 📚 Recursos

- **Kibana Documentation**: https://www.elastic.co/guide/en/kibana/current/index.html
- **Elasticsearch Documentation**: https://www.elastic.co/guide/en/elasticsearch/reference/current/index.html
- **Logstash Documentation**: https://www.elastic.co/guide/en/logstash/current/index.html

## ✅ Checklist

- [x] Elasticsearch configurado y funcionando
- [x] Kibana configurado y funcionando
- [x] Logstash configurado y funcionando
- [ ] Data view creado en Kibana (`pharmago-logs-*`)
- [ ] Dashboard básico creado
- [ ] Logs verificados en Kibana

## 💡 Próximos Pasos

1. **Crear data view** en Kibana: Stack Management → Data Views → Create data view (`pharmago-logs-*`)
2. **Explorar logs** en Discover
3. **Crear dashboards** personalizados
4. **Configurar alertas** (opcional)
