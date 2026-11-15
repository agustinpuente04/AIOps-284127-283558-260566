#!/bin/bash
# Script para limpiar/eliminar todos los recursos de PharmaGo en Kubernetes
# Equivalente a: docker-compose down
# Uso: ./cleanup.sh [--keep-volumes]

KEEP_VOLUMES=false

# Parsear argumentos
while [[ $# -gt 0 ]]; do
    case $1 in
        --keep-volumes)
            KEEP_VOLUMES=true
            shift
            ;;
        *)
            echo "Uso: $0 [--keep-volumes]"
            exit 1
            ;;
    esac
done

echo "=== Limpiando recursos de PharmaGo en Kubernetes ==="

# Verificar que kubectl está disponible
if ! command -v kubectl &> /dev/null; then
    echo "Error: kubectl no está instalado o no está en el PATH"
    exit 1
fi

# Eliminar namespace completo (equivalente a docker-compose down)
echo ""
echo "Eliminando namespace 'pharmago'..."
kubectl delete namespace pharmago --ignore-not-found=true 2>/dev/null || true

echo "Esperando a que el namespace termine de eliminarse..."
for i in $(seq 1 24); do
    if ! kubectl get namespace pharmago &>/dev/null; then
        echo "Namespace eliminado."
        break
    fi
    sleep 5
    echo "  Esperando... ($((i*5))s)"
done

# Eliminar PersistentVolumes (están fuera del namespace, deben estar Released)
if [ "$KEEP_VOLUMES" = false ]; then
    echo ""
    echo "Eliminando PersistentVolumes..."
    sleep 2
    for pv in sql-pv elasticsearch-pv prometheus-pv grafana-pv; do
        status=$(kubectl get pv $pv -o jsonpath='{.status.phase}' 2>/dev/null || echo "NotFound")
        if [ "$status" = "Released" ] || [ "$status" = "Failed" ] || [ "$status" = "Available" ] || [ "$status" = "NotFound" ]; then
            kubectl delete pv $pv --ignore-not-found=true 2>/dev/null || true
        fi
    done
    echo "PersistentVolumes eliminados"
else
    echo ""
    echo "PersistentVolumes conservados (usar --keep-volumes para conservarlos)"
fi

echo ""
echo "=== Limpieza completada ==="
echo ""
echo "Para verificar que todo fue eliminado:"
echo "  kubectl get all -n pharmago"
echo "  kubectl get pv"

