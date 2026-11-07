#!/bin/bash
# Script de despliegue para Kubernetes (Minikube)
# Uso: ./apply-k8s.sh

set -e

echo "=== Desplegando PharmaGo en Kubernetes ==="

# Verificar que kubectl está disponible
if ! command -v kubectl &> /dev/null; then
    echo "Error: kubectl no está instalado o no está en el PATH"
    exit 1
fi

# Verificar que minikube está corriendo
if ! minikube status &> /dev/null; then
    echo "Error: Minikube no está corriendo."
    echo "Ejecuta: minikube start --memory=6144 --cpus=4"
    exit 1
fi

echo ""
echo "1. Etiquetando nodo (requerido para que los pods se programen)..."
NODE_NAME=$(kubectl get nodes -o jsonpath='{.items[0].metadata.name}' 2>/dev/null)
if [ -n "$NODE_NAME" ]; then
    kubectl label nodes $NODE_NAME node-type=all --overwrite 2>/dev/null || true
    echo "   Nodo $NODE_NAME etiquetado con node-type=all"
else
    echo "   No se pudo obtener el nombre del nodo. Continuando..."
fi

echo ""
echo "2. Creando namespace..."
kubectl apply -f namespace.yaml

echo ""
echo "3. Creando secrets..."
kubectl apply -f secrets/db-secret.yaml

# Webhook de Discord para las alertas de Grafana. La URL no se versiona: se toma de la variable
# ALERT_WEBHOOK_URL de tu shell (export ALERT_WEBHOOK_URL=https://discord.com/api/webhooks/...).
# Sin ella se usa un placeholder: las alertas se evalúan y se ven en Grafana, pero no se envían.
if [ -z "$ALERT_WEBHOOK_URL" ]; then
    echo "   ALERT_WEBHOOK_URL no definida: las alertas no se enviarán a Discord (solo se verán en Grafana)."
fi
kubectl create secret generic grafana-alerting -n pharmago \
    --from-literal=webhook_url="${ALERT_WEBHOOK_URL:-http://127.0.0.1:9/disabled}" \
    --dry-run=client -o yaml | kubectl apply -f -

echo ""
echo "4. Creando configmaps..."
kubectl apply -f configmaps/prometheus-config.yaml
kubectl apply -f configmaps/otel-collector-config.yaml
kubectl apply -f configmaps/grafana-provisioning.yaml
kubectl apply -f configmaps/grafana-dashboards.yaml
kubectl apply -f configmaps/grafana-dashboard-infra.yaml
kubectl apply -f configmaps/grafana-dashboard-app.yaml
kubectl apply -f configmaps/grafana-alerting.yaml
kubectl apply -f configmaps/fluent-bit-config.yaml

echo ""
echo "5. Creando StorageClass y PersistentVolumes..."
kubectl apply -f persistent-volumes/storage-class.yaml

# Eliminar solo PVs en estado Released (huérfanos tras borrar el namespace).
# No intentar borrar PVs Bound: kubectl delete se bloquearía indefinidamente.
echo "   Limpiando PVs huérfanos (Released)..."
for pv in sql-pv elasticsearch-pv prometheus-pv grafana-pv; do
  status=$(kubectl get pv $pv -o jsonpath='{.status.phase}' 2>/dev/null || echo "NotFound")
  if [ "$status" = "Released" ] || [ "$status" = "Failed" ]; then
    kubectl delete pv $pv --ignore-not-found=true 2>/dev/null || true
  fi
done
sleep 2

kubectl apply -f persistent-volumes/sql-pv.yaml
kubectl apply -f persistent-volumes/elasticsearch-pv.yaml
kubectl apply -f persistent-volumes/prometheus-pv.yaml
kubectl apply -f persistent-volumes/grafana-pv.yaml

echo ""
echo "6. Desplegando base de datos..."
kubectl apply -f services/ops/db-service.yaml
kubectl apply -f deployments/ops/db-deployment.yaml

echo ""
echo "   Esperando a que la base de datos esté lista..."
timeout=0
max_timeout=300
while [ $timeout -lt $max_timeout ]; do
    pod_ready=$(kubectl get pod -l app=pharmago-db -n pharmago -o jsonpath='{.items[0].status.conditions[?(@.type=="Ready")].status}' 2>/dev/null)
    if [ "$pod_ready" = "True" ]; then
        echo "   Base de datos lista!"
        break
    fi
    sleep 5
    timeout=$((timeout + 5))
    echo "   Esperando... ($timeout/$max_timeout segundos)"
done
if [ $timeout -ge $max_timeout ]; then
    echo "   Timeout esperando la base de datos. Continuando..."
fi

echo ""
echo "7. Desplegando servicios de observabilidad..."
# Elasticsearch primero
kubectl apply -f services/ops/elasticsearch-service.yaml
kubectl apply -f deployments/ops/elasticsearch-deployment.yaml

# Esperar a que Elasticsearch esté listo
echo "   Esperando a que Elasticsearch esté listo..."
timeout=0
max_timeout=300
while [ $timeout -lt $max_timeout ]; do
    pod_ready=$(kubectl get pod -l app=elasticsearch -n pharmago -o jsonpath='{.items[0].status.conditions[?(@.type=="Ready")].status}' 2>/dev/null)
    if [ "$pod_ready" = "True" ]; then
        echo "   Elasticsearch listo!"
        break
    fi
    sleep 5
    timeout=$((timeout + 5))
    echo "   Esperando... ($timeout/$max_timeout segundos)"
done
if [ $timeout -ge $max_timeout ]; then
    echo "   Timeout esperando Elasticsearch. Continuando..."
fi

# Resto de servicios ops
kubectl apply -f services/ops/otel-collector-service.yaml
kubectl apply -f deployments/ops/otel-collector-deployment.yaml

kubectl apply -f deployments/ops/prometheus-serviceaccount.yaml
kubectl apply -f deployments/ops/prometheus-clusterrole.yaml
kubectl apply -f deployments/ops/prometheus-clusterrolebinding.yaml
kubectl apply -f services/ops/prometheus-service.yaml
kubectl apply -f deployments/ops/prometheus-deployment.yaml
kubectl apply -f services/ops/node-exporter-service.yaml
kubectl apply -f deployments/ops/node-exporter-daemonset.yaml

kubectl apply -f services/ops/grafana-service.yaml
kubectl apply -f deployments/ops/grafana-deployment.yaml
# Los dashboards y alertas se montan con subPath, que no se actualiza solo al cambiar el ConfigMap.
kubectl rollout restart deployment/grafana -n pharmago

kubectl apply -f services/ops/kibana-service.yaml
kubectl apply -f deployments/ops/kibana-deployment.yaml

# Fluent Bit: recolecta logs de pods y los envía a Elasticsearch (pharmago-logs-*)
kubectl apply -f deployments/ops/fluent-bit-serviceaccount.yaml
kubectl apply -f deployments/ops/fluent-bit-clusterrole.yaml
kubectl apply -f deployments/ops/fluent-bit-clusterrolebinding.yaml
kubectl apply -f deployments/ops/fluent-bit-daemonset.yaml

echo ""
echo "8. Desplegando servicios backend..."
kubectl apply -f services/backend/users-service-service.yaml
kubectl apply -f deployments/backend/users-service-deployment.yaml

kubectl apply -f services/backend/pharmacy-service-service.yaml
kubectl apply -f deployments/backend/pharmacy-service-deployment.yaml

kubectl apply -f services/backend/api-gateway-service.yaml
kubectl apply -f deployments/backend/api-gateway-deployment.yaml

echo ""
echo "9. Desplegando frontend..."
kubectl apply -f services/frontend/ui-service.yaml
kubectl apply -f deployments/frontend/ui-deployment.yaml

echo ""
echo "10. Aplicando PodDisruptionBudgets (alta disponibilidad)..."
kubectl apply -f pdb/app-pdbs.yaml

echo ""
echo "=== Despliegue completado ==="
echo ""
echo "Verificando estado de los pods..."
kubectl get pods -n pharmago -o wide

echo ""
echo "Para acceder a los servicios (usa 127.0.0.1 en Windows):"
echo "  ./port-forward.sh"
echo ""
echo "O con minikube service:"
echo "  Frontend:     minikube service pharmago-ui -n pharmago --url"
echo "                (usar esta URL para la app: balancea entre réplicas y no se corta"
echo "                 al reemplazar pods; port-forward queda atado a un único pod)"
echo "  Grafana:      minikube service grafana -n pharmago --url"
echo "  Kibana:       minikube service kibana -n pharmago --url"
echo "  Prometheus:   minikube service prometheus -n pharmago --url"

