# Guía de Persistencia de Datos - PharmaGo

## 📦 Volúmenes Persistentes Configurados

Todos los datos importantes se guardan en volúmenes de Docker que persisten entre reinicios:

### 1. Base de Datos SQL Server
- **Volumen:** `sql_data`
- **Ruta en contenedor:** `/var/opt/mssql`
- **Contenido:** Base de datos PharmaGo completa
- **Persistencia:** ✅ Datos se mantienen al reiniciar

### 2. Prometheus (Métricas)
- **Volumen:** `prometheus_data`
- **Ruta en contenedor:** `/prometheus`
- **Contenido:** Todas las métricas históricas
- **Retención:** 30 días (configurable)
- **Persistencia:** ✅ Métricas se mantienen al reiniciar

### 3. Grafana (Dashboards y Configuración)
- **Volumen:** `grafana_data`
- **Ruta en contenedor:** `/var/lib/grafana`
- **Contenido:** Dashboards, datasources, usuarios, configuración
- **Persistencia:** ✅ Dashboards se mantienen al reiniciar

## 🔄 Cómo Funciona

### Ciclo de Vida de los Datos

```bash
# 1. Primera vez - Crear volúmenes
docker-compose up -d
# → Se crean los volúmenes: sql_data, prometheus_data, grafana_data

# 2. Usar la aplicación
# → Los datos se escriben en los volúmenes

# 3. Apagar servicios
docker-compose down
# → Los contenedores se eliminan
# → Los volúmenes persisten

# 4. Volver a levantar
docker-compose up -d
# → Los contenedores se recrean
# → Los volúmenes se montan nuevamente
# → Los datos están disponibles
```

### ✅ Qué se Mantiene

| Componente | Datos Persistentes |
|------------|-------------------|
| **SQL Server** | Usuarios, farmacias, drogas, compras, sesiones |
| **Prometheus** | Métricas de los últimos 30 días |
| **Grafana** | Dashboards creados, datasources configurados, usuarios |

### ❌ Qué NO se Mantiene

| Componente | Datos Efímeros |
|------------|----------------|
| **Servicios (.NET)** | Logs en memoria, estado de aplicación |
| **OTLP Collector** | Buffer temporal de métricas |
| **Frontend (Angular)** | Estado de la aplicación en el navegador |

## ⚙️ Configuración de Prometheus

### Retención de Datos

Por defecto, Prometheus guarda métricas por **30 días**:

```yaml
command:
  - '--storage.tsdb.retention.time=30d'
```

**Cambiar retención:**

```yaml
# 7 días
- '--storage.tsdb.retention.time=7d'

# 90 días
- '--storage.tsdb.retention.time=90d'

# 1 año
- '--storage.tsdb.retention.time=365d'

# Por tamaño (máximo 10GB)
- '--storage.tsdb.retention.size=10GB'
```

### Espacio en Disco

**Estimación de uso:**

| Retención | Métricas/seg | Espacio Aproximado |
|-----------|--------------|-------------------|
| 7 días | 100 | ~500 MB |
| 30 días | 100 | ~2 GB |
| 90 días | 100 | ~6 GB |
| 1 año | 100 | ~25 GB |

## 🗂️ Gestión de Volúmenes

### Ver Volúmenes Existentes

```bash
# Listar todos los volúmenes
docker volume ls

# Ver detalles de un volumen específico
docker volume inspect codigo_prometheus_data
docker volume inspect codigo_grafana_data
docker volume inspect codigo_sql_data
```

### Ubicación de los Volúmenes

**En Windows:**
```
C:\ProgramData\Docker\volumes\codigo_prometheus_data\_data
C:\ProgramData\Docker\volumes\codigo_grafana_data\_data
C:\ProgramData\Docker\volumes\codigo_sql_data\_data
```

**En Linux/Mac:**
```
/var/lib/docker/volumes/codigo_prometheus_data/_data
/var/lib/docker/volumes/codigo_grafana_data/_data
/var/lib/docker/volumes/codigo_sql_data/_data
```

### Backup de Volúmenes

#### Backup Manual

```bash
# Backup de Prometheus
docker run --rm -v codigo_prometheus_data:/data -v $(pwd):/backup alpine tar czf /backup/prometheus_backup.tar.gz -C /data .

# Backup de Grafana
docker run --rm -v codigo_grafana_data:/data -v $(pwd):/backup alpine tar czf /backup/grafana_backup.tar.gz -C /data .

# Backup de SQL Server
docker run --rm -v codigo_sql_data:/data -v $(pwd):/backup alpine tar czf /backup/sql_backup.tar.gz -C /data .
```

#### Restaurar desde Backup

```bash
# Restaurar Prometheus
docker run --rm -v codigo_prometheus_data:/data -v $(pwd):/backup alpine sh -c "cd /data && tar xzf /backup/prometheus_backup.tar.gz"

# Restaurar Grafana
docker run --rm -v codigo_grafana_data:/data -v $(pwd):/backup alpine sh -c "cd /data && tar xzf /backup/grafana_backup.tar.gz"

# Restaurar SQL Server
docker run --rm -v codigo_sql_data:/data -v $(pwd):/backup alpine sh -c "cd /data && tar xzf /backup/sql_backup.tar.gz"
```

### Limpiar Volúmenes (⚠️ Cuidado)

```bash
# Eliminar un volumen específico (debe estar sin usar)
docker volume rm codigo_prometheus_data

# Eliminar todos los volúmenes no usados
docker volume prune

# Eliminar TODOS los volúmenes (incluso los en uso)
docker-compose down -v
```

## 🔍 Verificar Persistencia

### Test de Persistencia

```bash
# 1. Levantar servicios
docker-compose up -d

# 2. Generar datos de prueba
curl http://localhost:5000/api/drug
# (hacer varias peticiones)

# 3. Verificar métricas en Prometheus
curl http://localhost:9090/api/v1/query?query=pharmago_http_requests_total

# 4. Apagar servicios
docker-compose down

# 5. Volver a levantar
docker-compose up -d

# 6. Verificar que las métricas siguen ahí
curl http://localhost:9090/api/v1/query?query=pharmago_http_requests_total
# → Debería mostrar las métricas anteriores
```

### Ver Tamaño de Volúmenes

```bash
# Ver tamaño de todos los volúmenes
docker system df -v

# Ver solo volúmenes de PharmaGo
docker system df -v | grep codigo
```

## 📊 Monitoreo de Espacio

### Query de Prometheus para Ver Uso de Disco

```promql
# Tamaño de la base de datos de Prometheus (bytes)
prometheus_tsdb_storage_blocks_bytes

# Número de series temporales almacenadas
prometheus_tsdb_head_series

# Número de chunks en memoria
prometheus_tsdb_head_chunks
```

### Alertas Recomendadas

```yaml
# Alerta si Prometheus usa más del 80% del espacio
- alert: PrometheusStorageAlmostFull
  expr: (prometheus_tsdb_storage_blocks_bytes / 10737418240) > 0.8
  for: 5m
  annotations:
    summary: "Prometheus storage casi lleno"
    description: "Prometheus está usando {{ $value | humanizePercentage }} del espacio disponible"
```

## 🔧 Configuración Avanzada

### Prometheus con Retención por Tamaño y Tiempo

```yaml
prometheus:
  command:
    - '--storage.tsdb.retention.time=30d'
    - '--storage.tsdb.retention.size=10GB'
```

**Comportamiento:** Prometheus eliminará datos cuando se cumpla **cualquiera** de las dos condiciones.

### Compresión de Datos

Prometheus comprime automáticamente los datos antiguos. No requiere configuración adicional.

### Grafana con Provisioning Automático

✅ **Ya configurado!** Grafana se configura automáticamente al iniciar:

```yaml
grafana:
  volumes:
    - grafana_data:/var/lib/grafana
    - ./grafana/provisioning:/etc/grafana/provisioning
```

**Archivos de provisioning incluidos:**
- `grafana/provisioning/datasources/prometheus.yml` - Datasource de Prometheus
- `grafana/provisioning/dashboards/dashboards.yml` - Configuración de dashboards
- `grafana/provisioning/dashboards/pharmago-overview.json` - Dashboard de ejemplo

**Al levantar Grafana:**
1. Se conecta automáticamente a Prometheus (`http://prometheus:9090`)
2. Se carga el dashboard "PharmaGo - Overview"
3. No necesitas configurar nada manualmente

## 🚨 Troubleshooting

### Problema: Volúmenes no se crean

```bash
# Verificar que docker-compose.yml tiene la sección volumes
docker-compose config | grep -A 5 volumes

# Crear volúmenes manualmente
docker volume create codigo_prometheus_data
docker volume create codigo_grafana_data
docker volume create codigo_sql_data
```

### Problema: Datos se pierden al reiniciar

```bash
# Verificar que los volúmenes están montados
docker inspect pharmago-db | grep Mounts -A 20
docker inspect prometheus | grep Mounts -A 20
docker inspect grafana | grep Mounts -A 20

# Verificar que NO estás usando docker-compose down -v
# (el flag -v elimina los volúmenes)
```

### Problema: Prometheus dice "out of disk space"

```bash
# Ver espacio usado
docker exec prometheus df -h /prometheus

# Reducir retención
# Editar docker-compose.yml y cambiar:
- '--storage.tsdb.retention.time=7d'

# Reiniciar Prometheus
docker-compose restart prometheus
```

### Problema: Grafana perdió los dashboards

```bash
# Verificar que el volumen existe
docker volume inspect codigo_grafana_data

# Si el volumen existe pero está vacío, restaurar desde backup
docker run --rm -v codigo_grafana_data:/data -v $(pwd):/backup alpine sh -c "cd /data && tar xzf /backup/grafana_backup.tar.gz"
```

## 📋 Checklist de Persistencia

### ✅ Verificación Inicial

- [ ] Volúmenes definidos en `docker-compose.yml`
- [ ] Volúmenes montados en los servicios correctos
- [ ] Permisos correctos en las rutas de volúmenes

### ✅ Después de Configurar

- [ ] Levantar servicios: `docker-compose up -d`
- [ ] Generar datos de prueba
- [ ] Apagar servicios: `docker-compose down`
- [ ] Volver a levantar: `docker-compose up -d`
- [ ] Verificar que los datos persisten

### ✅ Mantenimiento Regular

- [ ] Hacer backups periódicos (semanal/mensual)
- [ ] Monitorear espacio en disco
- [ ] Revisar logs de Prometheus y Grafana
- [ ] Ajustar retención según necesidades

## 🎯 Resumen

| Acción | Comando | Resultado |
|--------|---------|-----------|
| **Apagar servicios** | `docker-compose down` | ✅ Datos persisten |
| **Apagar y eliminar volúmenes** | `docker-compose down -v` | ❌ Datos se pierden |
| **Reiniciar servicios** | `docker-compose restart` | ✅ Datos persisten |
| **Rebuild servicios** | `docker-compose up --build` | ✅ Datos persisten |
| **Ver volúmenes** | `docker volume ls` | Lista todos los volúmenes |
| **Backup** | `docker run --rm -v ...` | Crea archivo .tar.gz |

### 🔑 Regla de Oro

**Usa `docker-compose down` (sin `-v`) para mantener los datos.**

**Solo usa `docker-compose down -v` si quieres empezar desde cero.**

