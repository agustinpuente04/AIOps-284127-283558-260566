#!/bin/bash
# Construir y cargar solo pharmago-ui en Minikube
# Uso: ./build-ui.sh

set -e

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
CODE_DIR="$(dirname "$SCRIPT_DIR")"

echo "Construyendo pharmago-ui..."
cd "$CODE_DIR/Frontend"
docker build -f Dockerfile -t pharmago-ui:latest .

echo "Cargando en Minikube..."
minikube image load pharmago-ui:latest

echo "Listo. Reiniciando deployment..."
kubectl rollout restart deployment pharmago-ui -n pharmago

echo ""
echo "Verificar: kubectl get pods -n pharmago -l app=pharmago-ui"
