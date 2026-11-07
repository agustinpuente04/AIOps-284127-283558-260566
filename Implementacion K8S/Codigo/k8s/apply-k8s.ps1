# Script de despliegue para Kubernetes (Minikube)
# Uso: .\apply-k8s.ps1

Write-Host "=== Desplegando PharmaGo en Kubernetes ===" -ForegroundColor Green

# Verificar que kubectl está disponible
if (-not (Get-Command kubectl -ErrorAction SilentlyContinue)) {
    Write-Host "Error: kubectl no está instalado o no está en el PATH" -ForegroundColor Red
    exit 1
}

# Verificar que el cluster Kubernetes está accesible
$nodes = kubectl get nodes -o jsonpath='{.items[*].metadata.name}' 2>&1
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrEmpty($nodes)) {
    Write-Host "Error: No hay cluster Kubernetes accesible. Si usas Minikube: minikube start (o minikube start --nodes 3)" -ForegroundColor Red
    exit 1
}

# Etiquetar nodos (requerido para que los pods de telemetría se programen)
Write-Host "`n1. Etiquetando nodos..." -ForegroundColor Yellow
$nodeList = $nodes.Trim().Split(" ", [StringSplitOptions]::RemoveEmptyEntries)
if ($nodeList.Count -eq 1) {
    Write-Host "   Cluster de 1 nodo detectado. Etiquetando $($nodeList[0]) con node-type=all" -ForegroundColor Cyan
    kubectl label nodes $nodeList[0] node-type=all --overwrite 2>$null
} elseif ($nodeList.Count -ge 3) {
    Write-Host "   Cluster multi-nodo detectado. Etiquetando nodos..." -ForegroundColor Cyan
    kubectl label nodes minikube node-type=frontend --overwrite 2>$null
    kubectl label nodes minikube-m02 node-type=backend --overwrite 2>$null
    kubectl label nodes minikube-m03 node-type=ops --overwrite 2>$null
} else {
    $firstNode = $nodeList[0]
    Write-Host "   Etiquetando nodo $firstNode con node-type=all (compatibilidad)" -ForegroundColor Cyan
    kubectl label nodes $firstNode node-type=all --overwrite 2>$null
}
Write-Host "   Verificar: kubectl get nodes --show-labels" -ForegroundColor Gray

Write-Host "`n2. Creando namespace..." -ForegroundColor Yellow
kubectl apply -f namespace.yaml
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "`n3. Creando secrets..." -ForegroundColor Yellow
kubectl apply -f secrets\db-secret.yaml
if ($LASTEXITCODE -ne 0) { exit 1 }

# Webhook de Discord para las alertas de Grafana. La URL no se versiona: se toma de la variable
# de entorno ALERT_WEBHOOK_URL ($env:ALERT_WEBHOOK_URL = "https://discord.com/api/webhooks/...").
# Sin ella se usa un placeholder: las alertas se evalúan y se ven en Grafana, pero no se envían.
$webhook = $env:ALERT_WEBHOOK_URL
if ([string]::IsNullOrEmpty($webhook)) {
    Write-Host "   ALERT_WEBHOOK_URL no definida: las alertas no se enviaran a Discord (solo se veran en Grafana)." -ForegroundColor Cyan
    $webhook = "http://127.0.0.1:9/disabled"
}
kubectl create secret generic grafana-alerting -n pharmago --from-literal=webhook_url="$webhook" --dry-run=client -o yaml | kubectl apply -f -
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "`n4. Creando configmaps..." -ForegroundColor Yellow
kubectl apply -f configmaps\prometheus-config.yaml
kubectl apply -f configmaps\otel-collector-config.yaml
kubectl apply -f configmaps\grafana-provisioning.yaml
kubectl apply -f configmaps\grafana-dashboards.yaml
kubectl apply -f configmaps\grafana-dashboard-infra.yaml
kubectl apply -f configmaps\grafana-dashboard-app.yaml
kubectl apply -f configmaps\grafana-alerting.yaml
kubectl apply -f configmaps\fluent-bit-config.yaml
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "`n5. Creando StorageClass y PersistentVolumes..." -ForegroundColor Yellow
kubectl apply -f persistent-volumes\storage-class.yaml
if ($LASTEXITCODE -ne 0) { exit 1 }

# Eliminar PVs existentes en estado Released para recrearlos
Write-Host "   Limpiando PVs existentes..." -ForegroundColor Cyan
kubectl delete pv sql-pv elasticsearch-pv prometheus-pv grafana-pv --ignore-not-found=true
Start-Sleep -Seconds 2

kubectl apply -f persistent-volumes\sql-pv.yaml
kubectl apply -f persistent-volumes\elasticsearch-pv.yaml
kubectl apply -f persistent-volumes\prometheus-pv.yaml
kubectl apply -f persistent-volumes\grafana-pv.yaml
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "`n6. Desplegando base de datos..." -ForegroundColor Yellow
kubectl apply -f services\ops\db-service.yaml
kubectl apply -f deployments\ops\db-deployment.yaml
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "`n   Esperando a que la base de datos esté lista..." -ForegroundColor Yellow
# Esperar a que el pod esté Ready
$timeout = 0
$maxTimeout = 300
do {
    $podReady = kubectl get pod -l app=pharmago-db -n pharmago -o jsonpath='{.items[0].status.conditions[?(@.type=="Ready")].status}' 2>&1
    if ($podReady -eq "True") {
        Write-Host "   Base de datos lista!" -ForegroundColor Green
        break
    }
    Start-Sleep -Seconds 5
    $timeout += 5
    if ($timeout -ge $maxTimeout) {
        Write-Host "   Timeout esperando la base de datos. Continuando..." -ForegroundColor Yellow
        break
    }
    Write-Host "   Esperando... ($timeout/$maxTimeout segundos)" -ForegroundColor Cyan
} while ($true)

Write-Host "`n7. Desplegando servicios de observabilidad..." -ForegroundColor Yellow
# Elasticsearch primero
kubectl apply -f services\ops\elasticsearch-service.yaml
kubectl apply -f deployments\ops\elasticsearch-deployment.yaml

# Esperar a que Elasticsearch esté listo
Write-Host "   Esperando a que Elasticsearch esté listo..." -ForegroundColor Yellow
$timeout = 0
$maxTimeout = 300
do {
    $podReady = kubectl get pod -l app=elasticsearch -n pharmago -o jsonpath='{.items[0].status.conditions[?(@.type=="Ready")].status}' 2>&1
    if ($podReady -eq "True") {
        Write-Host "   Elasticsearch listo!" -ForegroundColor Green
        break
    }
    Start-Sleep -Seconds 5
    $timeout += 5
    if ($timeout -ge $maxTimeout) {
        Write-Host "   Timeout esperando Elasticsearch. Continuando..." -ForegroundColor Yellow
        break
    }
    Write-Host "   Esperando... ($timeout/$maxTimeout segundos)" -ForegroundColor Cyan
} while ($true)

# Resto de servicios ops
kubectl apply -f services\ops\otel-collector-service.yaml
kubectl apply -f deployments\ops\otel-collector-deployment.yaml

kubectl apply -f deployments\ops\prometheus-serviceaccount.yaml
kubectl apply -f deployments\ops\prometheus-clusterrole.yaml
kubectl apply -f deployments\ops\prometheus-clusterrolebinding.yaml
kubectl apply -f services\ops\prometheus-service.yaml
kubectl apply -f deployments\ops\prometheus-deployment.yaml
kubectl apply -f services\ops\node-exporter-service.yaml
kubectl apply -f deployments\ops\node-exporter-daemonset.yaml

kubectl apply -f services\ops\grafana-service.yaml
kubectl apply -f deployments\ops\grafana-deployment.yaml
# Los dashboards y alertas se montan con subPath, que no se actualiza solo al cambiar el ConfigMap.
kubectl rollout restart deployment/grafana -n pharmago

kubectl apply -f services\ops\kibana-service.yaml
kubectl apply -f deployments\ops\kibana-deployment.yaml

if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "`n8. Desplegando servicios backend..." -ForegroundColor Yellow
kubectl apply -f services\backend\users-service-service.yaml
kubectl apply -f deployments\backend\users-service-deployment.yaml

kubectl apply -f services\backend\pharmacy-service-service.yaml
kubectl apply -f deployments\backend\pharmacy-service-deployment.yaml

kubectl apply -f services\backend\api-gateway-service.yaml
kubectl apply -f deployments\backend\api-gateway-deployment.yaml

if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "`n9. Desplegando frontend..." -ForegroundColor Yellow
kubectl apply -f services\frontend\ui-service.yaml
kubectl apply -f deployments\frontend\ui-deployment.yaml

if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "`n10. Aplicando PodDisruptionBudgets (alta disponibilidad)..." -ForegroundColor Yellow
kubectl apply -f pdb\app-pdbs.yaml

if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "`n=== Despliegue completado ===" -ForegroundColor Green
Write-Host "`nVerificando estado de los pods..." -ForegroundColor Yellow
kubectl get pods -n pharmago -o wide

Write-Host "`nPara ver los servicios expuestos:" -ForegroundColor Cyan
Write-Host "  Frontend:     minikube service pharmago-ui -n pharmago --url" -ForegroundColor White
Write-Host "                (usar esta URL para la app: balancea entre replicas y no se corta al reemplazar pods)" -ForegroundColor Gray
Write-Host "  Grafana:      minikube service grafana -n pharmago --url" -ForegroundColor White
Write-Host "  Kibana:       minikube service kibana -n pharmago --url" -ForegroundColor White
Write-Host "  Prometheus:   minikube service prometheus -n pharmago --url" -ForegroundColor White

