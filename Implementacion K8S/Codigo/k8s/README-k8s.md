# Despliegue de PharmaGo en Kubernetes (Minikube)

Esta guía describe cómo desplegar la aplicación PharmaGo en un cluster de Kubernetes usando Minikube con un solo nodo.

## Arquitectura

La aplicación se ejecuta en un único nodo (minikube) con todos los componentes:

- **Frontend**: pharmago-ui
- **Backend**: pharmago-api-gateway, pharmago-users-service, pharmago-pharmacy-service
- **Base de datos**: pharmago-db (SQL Server Express)
- **Telemetría y observabilidad**: otlp-collector, prometheus, grafana, elasticsearch, kibana, fluent-bit

**Requisito de memoria**: El nodo debe tener al menos 5-6GB de RAM para soportar Elasticsearch (1.5Gi), Kibana (2Gi), SQL Server Express (1.5Gi) y el resto de servicios.

## Prerrequisitos

1. **Minikube** instalado y configurado
2. **kubectl** instalado y configurado
3. **Docker** instalado (para construir las imágenes)

## Configuración Inicial

### 1. Crear cluster Minikube (un nodo, 6GB RAM)

```bash
# Crear cluster con suficiente memoria para telemetría (Elasticsearch, Kibana, etc.)
minikube start --memory=6144 --cpus=4

# Verificar nodo
kubectl get nodes
```

**Importante**: Usa al menos 6GB de RAM. Con menos, Elasticsearch y otros pods de observabilidad pueden fallar por OOM.

### 2. Etiquetar nodo

El script `apply-k8s.sh` etiqueta el nodo automáticamente con `node-type=all`. Si despliegas manualmente:
```bash
kubectl label nodes minikube node-type=all --overwrite
```

### 3. Construir y cargar imágenes Docker

**IMPORTANTE**: Usa `minikube image load` para cargar las imágenes en Minikube (no `minikube docker-env`).

#### Opción 1: Script automatizado (Recomendado)

```bash
cd k8s
chmod +x build-images.sh
./build-images.sh
```

#### Opción 2: Manual

Desde el directorio `Implementacion K8S/Codigo`:

```bash
# Construir imágenes de backend
cd Backend
docker build -f PharmaGo.UsersService/Dockerfile -t pharmago-users-service:latest .
minikube image load pharmago-users-service:latest

docker build -f PharmaGo.PharmacyService/Dockerfile -t pharmago-pharmacy-service:latest .
minikube image load pharmago-pharmacy-service:latest

docker build -f PharmaGo.ApiGateway/Dockerfile -t pharmago-api-gateway:latest .
minikube image load pharmago-api-gateway:latest

# Construir imagen de frontend
cd ../Frontend
docker build -f Dockerfile -t pharmago-ui:latest .
minikube image load pharmago-ui:latest
```

**Nota**: `minikube image load` carga las imágenes en el nodo de Minikube.

## Despliegue

**IMPORTANTE**: Asegúrate de haber construido y cargado las imágenes Docker primero (paso 3).

### Opción 1: Script automatizado (Recomendado)

```bash
cd k8s
chmod +x apply-k8s.sh
./apply-k8s.sh
```

### Opción 2: Despliegue manual

```bash
cd k8s

# 1. Namespace
kubectl apply -f namespace.yaml

# 2. Secrets
kubectl apply -f secrets/db-secret.yaml

# 3. ConfigMaps
kubectl apply -f configmaps/

# 4. PersistentVolumes
kubectl apply -f persistent-volumes/

# 5. Database
kubectl apply -f services/ops/db-service.yaml
kubectl apply -f deployments/ops/db-deployment.yaml
kubectl wait --for=condition=ready pod -l app=pharmago-db -n pharmago --timeout=300s

# 6. Ops services (Elasticsearch primero)
kubectl apply -f services/ops/elasticsearch-service.yaml
kubectl apply -f deployments/ops/elasticsearch-deployment.yaml
kubectl wait --for=condition=ready pod -l app=elasticsearch -n pharmago --timeout=300s

kubectl apply -f services/ops/
kubectl apply -f deployments/ops/

# 7. Backend services
kubectl apply -f services/backend/
kubectl apply -f deployments/backend/

# 8. Frontend
kubectl apply -f services/frontend/
kubectl apply -f deployments/frontend/
```

## Verificación

### Ver estado de los pods

```bash
# Ver todos los pods
kubectl get pods -n pharmago -o wide

# Ver pods por nodo (con un nodo, todos aparecen en minikube)
kubectl get pods -n pharmago -o wide --field-selector spec.nodeName=minikube
```

### Ver servicios

```bash
kubectl get svc -n pharmago
```

### Ver logs

```bash
# Logs de un pod específico
kubectl logs -n pharmago <pod-name>

# Logs con seguimiento
kubectl logs -n pharmago -f <pod-name>
```

## Acceso a los Servicios

Hay dos opciones para acceder a los servicios: **port-forward** (recomendado en Windows) o **minikube service**.

### Opción 1: Port-forward (Recomendado en Windows)

En Windows, `localhost` puede fallar con `kubectl port-forward` y herramientas como `sqlcmd`. Usa **127.0.0.1** para todas las conexiones.

```bash
cd k8s
./port-forward.sh
```

Servicios disponibles en:
- **Frontend**: http://127.0.0.1:4200
- **API Gateway**: http://127.0.0.1:5000
- **BD (SQL Server)**: `127.0.0.1,11433` (sa / Str0ngP@ssword!)
- **Prometheus**: http://127.0.0.1:9090
- **Grafana**: http://127.0.0.1:3000 (admin/admin)
- **Kibana**: http://127.0.0.1:5601

Para detener: `./port-forward.sh --stop`

### Opción 2: Minikube service

### Frontend

```bash
minikube service pharmago-ui -n pharmago --url
```

### Grafana

```bash
minikube service grafana -n pharmago --url
# Credenciales: admin / admin
```

### Kibana

```bash
minikube service kibana -n pharmago --url
```

### Prometheus

```bash
minikube service prometheus -n pharmago --url
```

## Configuración de Replicas

Para cambiar el número de réplicas de cualquier componente, edita el archivo de deployment correspondiente:

```yaml
spec:
  replicas: 1  # Cambiar este valor
```

Luego aplica los cambios:

```bash
kubectl apply -f deployments/<component>/<deployment>.yaml
```

> Los componentes de aplicación (UI, gateway, users, pharmacy) necesitan **al menos 2 réplicas** para cumplir con la alta disponibilidad. Con 1 réplica, el PodDisruptionBudget bloquearía `kubectl drain`.

## Alta disponibilidad: reemplazo de componentes sin downtime

**Requisito:** el sistema debe poder reemplazar sus componentes garantizando 100% de disponibilidad.

### Cómo se cumple

| Mecanismo | Dónde | Qué garantiza |
|---|---|---|
| `replicas: 2` | Deployments de UI, gateway, users y pharmacy | Siempre hay otra réplica sirviendo si se pierde un pod |
| `RollingUpdate` con `maxSurge: 1`, `maxUnavailable: 0` y `minReadySeconds: 5` | Mismos Deployments | El pod viejo solo se baja cuando el nuevo pasó la readiness probe |
| `readinessProbe` y `startupProbe` en `/health` | Mismos Deployments | Un pod no recibe tráfico hasta estar listo |
| `preStop: sleep 10` | Mismos Deployments | kube-proxy saca el pod de los Endpoints **antes** de que reciba SIGTERM |
| `HostOptions.ShutdownTimeout = 25s` | `Program.cs` de los 3 servicios .NET | Las requests en curso terminan antes de que se cierre el proceso |
| `terminationGracePeriodSeconds: 40` (backend) / `30` (UI) | Deployments | Margen > preStop + apagado ordenado |
| `PooledConnectionIdleTimeout = 5s` en YARP | `PharmaGo.ApiGateway/Program.cs` | Las conexiones keep-alive hacia un pod que se va caducan durante su preStop |
| `keepalive_timeout 5s` | `Frontend/nginx.conf` | Lo mismo para las conexiones navegador → UI |
| PodDisruptionBudget `minAvailable: 1` | `pdb/app-pdbs.yaml` | Un `drain` o una eviction nunca deja una app sin réplicas |
| Anti-afinidad *preferred* por app | Deployments | Si hay varios nodos, las réplicas se reparten entre ellos |
| nginx proxy `/api/` → Service del gateway | `Frontend/nginx.conf` | Único punto de entrada estable (NodePort que balancea entre réplicas) |

Secuencia al reemplazar un pod: pod nuevo → `startup` y `readiness` OK → entra a los Endpoints → pod viejo pasa a `Terminating` → sale de los Endpoints y espera el preStop de 10 s → SIGTERM → termina las requests en curso → fin.

### Actualizar un componente sin downtime

Como las imágenes usan `:latest` con `imagePullPolicy: Never`, después de reconstruir y cargar una imagen nueva hay que forzar el rollout:

```bash
./build-images.sh          # o build-images.ps1
kubectl rollout restart deployment/pharmago-api-gateway -n pharmago
kubectl rollout status  deployment/pharmago-api-gateway -n pharmago
```

### Verificación

```bash
URL=$(minikube service pharmago-ui -n pharmago --url)   # en Windows: dejar la terminal abierta y copiar la URL
cd ../chaos-engineering
./zero-downtime-test.sh "$URL" 180 --rollout    # reinicia las 4 apps mientras mide; esperado: 100%
```

Prueba de falla abrupta: con el script corriendo, borra **un** pod a la vez en otra terminal:

```bash
kubectl delete pod -n pharmago $(kubectl get pod -n pharmago -l app=pharmago-api-gateway -o name | head -1 | cut -d/ -f2) --wait=false
```

Para revisar los PDBs: `kubectl get pdb -n pharmago` (cada uno debe mostrar `ALLOWED DISRUPTIONS = 1`).

### Limitaciones conocidas

- **Base de datos (`pharmago-db`)**: es un único SQL Server Express sobre un PV `ReadWriteOnce`, así que es un punto único de falla y queda fuera del alcance. Express no soporta Always On ni réplicas. Los servicios toleran reinicios cortos gracias al retry con backoff y al circuit breaker de Polly, pero un reemplazo de la BD sí implica indisponibilidad.
- **Observabilidad** (Prometheus, Grafana, Elasticsearch, Kibana, OTel Collector): fuera del alcance. Su caída no afecta a los usuarios.
- **Rate limiting**: los contadores están en memoria de cada réplica del gateway. Con 2 réplicas, el límite efectivo por IP es aproximadamente el doble del configurado.
- **Migraciones EF**: `MigrateDatabase()` corre al arrancar. En un despliegue desde cero, las 2 réplicas pueden intentar migrar a la vez. Si una falla, se reinicia y encuentra la migración ya aplicada.

## Troubleshooting

### Pods no se inician (Pending / telemetría)

Si los pods de telemetría (otel-collector, prometheus, grafana, elasticsearch, kibana, fluent-bit) quedan en `Pending`:

1. **Falta etiqueta en el nodo**: Los pods requieren `node-type=all`.
   - Usa el script `apply-k8s.sh` que etiqueta automáticamente, o
   - Manual: `kubectl label nodes minikube node-type=all --overwrite`
   
   ```bash
   kubectl get nodes --show-labels
   ```

2. Verificar eventos del pod:
   ```bash
   kubectl describe pod <pod-name> -n pharmago
   ```

3. Ver logs:
   ```bash
   kubectl logs <pod-name> -n pharmago
   ```

### Imágenes no encontradas

Si los pods fallan con `ImagePullBackOff`:

1. Verificar que las imágenes están cargadas en Minikube:
   ```bash
   minikube image ls | grep pharmago
   ```

2. Si faltan imágenes, reconstruirlas y cargarlas:
   ```bash
   ./build-images.sh
   ```

3. Verificar que los deployments usan `imagePullPolicy: Never` (ya configurado).

### Base de datos no está lista

Si los servicios backend no pueden conectarse a la base de datos:

1. Verificar que el pod de la base de datos está corriendo:
   ```bash
   kubectl get pods -n pharmago -l app=pharmago-db
   ```

2. Verificar logs de la base de datos:
   ```bash
   kubectl logs -n pharmago -l app=pharmago-db
   ```

3. Verificar el secret:
   ```bash
   kubectl get secret db-secret -n pharmago -o yaml
   ```

### PersistentVolumes no se montan

En Minikube, los PersistentVolumes usan `hostPath` con `DirectoryOrCreate`, que crea los directorios automáticamente. Si tienes problemas:

```bash
# Conectarse al nodo y crear directorios manualmente
minikube ssh
sudo mkdir -p /mnt/data/{sql,elasticsearch,prometheus,grafana}
sudo chmod 777 /mnt/data/{sql,elasticsearch,prometheus,grafana}
exit
```

**Nota**: El deployment de SQL Server incluye un initContainer que configura los permisos para el usuario `mssql` (UID 10001).

### Elasticsearch OOMKilled (Out of Memory)

Si Elasticsearch está en `CrashLoopBackOff` con `OOMKilled`:

1. **Verificar memoria disponible en el nodo:**
   ```bash
   kubectl describe node minikube | grep -A 5 "Allocated resources"
   ```

2. **Aumentar memoria de Minikube:**
   ```bash
   minikube stop
   minikube delete
   minikube start --memory=6144 --cpus=4
   ```

3. **Reducir memoria heap de Elasticsearch:**
   Edita `deployments/ops/elasticsearch-deployment.yaml` y reduce `ES_JAVA_OPTS`:
   ```yaml
   - name: ES_JAVA_OPTS
     value: "-Xms1g -Xmx1g"  # Reducir de 2g a 1g
   ```
   Luego aumenta el límite de memoria del contenedor a 3-4Gi para compensar.

4. **Deshabilitar características que consumen memoria:**
   Agrega estas variables de entorno en el deployment:
   ```yaml
   - name: xpack.monitoring.collection.enabled
     value: "false"
   - name: xpack.security.enabled
     value: "false"  # Ya está configurado
   ```

5. **Verificar logs:**
   ```bash
   kubectl logs -n pharmago -l app=elasticsearch --tail=50
   ```

**Nota**: Elasticsearch requiere al menos 2GB de heap de Java y memoria adicional para el sistema operativo y otros procesos. En un entorno con recursos limitados (Minikube), considera usar una versión más ligera o deshabilitar características no esenciales.

### Kibana no está Ready

Si Kibana está `Running` pero no `Ready`:

1. **Verificar que Elasticsearch está funcionando:**
   ```bash
   kubectl get pods -n pharmago -l app=elasticsearch
   ```

2. **Verificar logs de Kibana:**
   ```bash
   kubectl logs -n pharmago -l app=kibana --tail=50
   ```

3. **Kibana espera a Elasticsearch:** Si Elasticsearch no está listo, Kibana no puede conectarse y permanecerá en `Running` pero no `Ready`.

## Limpieza (Equivalente a `docker-compose down`)

### Opción 1: Script automatizado (Recomendado)

```bash
cd k8s
chmod +x cleanup.sh
./cleanup.sh
```

**Conservar volúmenes persistentes:**
```bash
./cleanup.sh --keep-volumes
```

### Opción 2: Comando manual

```bash
# Eliminar namespace (elimina todos los recursos dentro de él)
kubectl delete namespace pharmago

# Eliminar PersistentVolumes (están fuera del namespace)
kubectl delete pv sql-pv elasticsearch-pv prometheus-pv grafana-pv
```

### Opción 3: Eliminar recursos individualmente

```bash
# Eliminar recursos en orden inverso al despliegue
kubectl delete -f deployments/
kubectl delete -f services/
kubectl delete -f configmaps/
kubectl delete -f secrets/
kubectl delete -f persistent-volumes/
kubectl delete -f namespace.yaml
```

**Nota**: Eliminar el namespace es la forma más simple y equivalente a `docker-compose down`, ya que elimina todos los recursos (pods, services, deployments, configmaps, secrets, PVCs) de una vez. Los PersistentVolumes deben eliminarse por separado ya que están fuera del namespace.

## Notas Importantes

1. **Logs**: Fluent Bit (DaemonSet) recolecta los logs de los pods y los envía directamente a Elasticsearch con índice `pharmago-logs-*`. Kibana se usa para visualizarlos.

2. **PersistentVolumes**: Los PVs usan `hostPath` que es adecuado para desarrollo pero no para producción. En producción, usa storage classes apropiadas.

3. **Secrets**: La contraseña de la base de datos está en texto plano en el secret. Para producción, considera usar herramientas como Sealed Secrets o external secret managers.

4. **Health Checks**: Los deployments incluyen health checks básicos. Asegúrate de que tus aplicaciones expongan endpoints `/health` o ajusta las configuraciones según corresponda.

5. **Recursos**: Los límites de recursos están configurados de forma conservadora. Ajusta según las necesidades de tu entorno.

## Estructura de Archivos

```
k8s/
├── namespace.yaml
├── configmaps/
│   ├── prometheus-config.yaml
│   ├── otel-collector-config.yaml
│   ├── grafana-provisioning.yaml
│   └── fluent-bit-config.yaml
├── secrets/
│   └── db-secret.yaml
├── persistent-volumes/
│   ├── sql-pv.yaml
│   ├── elasticsearch-pv.yaml
│   ├── prometheus-pv.yaml
│   └── grafana-pv.yaml
├── deployments/
│   ├── frontend/
│   │   └── ui-deployment.yaml
│   ├── backend/
│   │   ├── api-gateway-deployment.yaml
│   │   ├── users-service-deployment.yaml
│   │   └── pharmacy-service-deployment.yaml
│   └── ops/
│       ├── db-deployment.yaml
│       ├── otel-collector-deployment.yaml
│       ├── prometheus-deployment.yaml
│       ├── grafana-deployment.yaml
│       ├── elasticsearch-deployment.yaml
│       ├── kibana-deployment.yaml
│       └── fluent-bit-daemonset.yaml
├── services/
│   ├── frontend/
│   │   └── ui-service.yaml
│   ├── backend/
│   │   ├── api-gateway-service.yaml
│   │   ├── users-service-service.yaml
│   │   └── pharmacy-service-service.yaml
│   └── ops/
│       ├── db-service.yaml
│       ├── otel-collector-service.yaml
│       ├── prometheus-service.yaml
│       ├── grafana-service.yaml
│       ├── elasticsearch-service.yaml
│       └── kibana-service.yaml
├── build-images.sh
├── apply-k8s.sh
├── cleanup.sh
└── README-k8s.md
```

