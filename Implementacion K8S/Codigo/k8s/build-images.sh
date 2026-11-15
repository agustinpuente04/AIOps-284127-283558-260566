#!/bin/bash
# Script para construir y cargar imágenes Docker en Minikube (multi-nodo)
# Funciona en Linux y Mac
# Uso: ./build-images.sh

set -e

echo "=== Construyendo y cargando imágenes Docker en Minikube ==="

# Verificar que docker está disponible
if ! command -v docker &> /dev/null; then
    echo "Error: Docker no está instalado o no está en el PATH"
    exit 1
fi

# Verificar que minikube está corriendo
if ! minikube status &> /dev/null; then
    echo "Error: Minikube no está corriendo. Ejecuta: minikube start --nodes 3"
    exit 1
fi

# Obtener el directorio base (donde está este script)
SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
CODE_DIR="$(dirname "$SCRIPT_DIR")"

echo ""
echo "Construyendo imágenes de backend..."
cd "$CODE_DIR/Backend"

echo "  - pharmago-users-service..."
docker build -f PharmaGo.UsersService/Dockerfile -t pharmago-users-service:latest .
minikube image load pharmago-users-service:latest

echo "  - pharmago-pharmacy-service..."
docker build -f PharmaGo.PharmacyService/Dockerfile -t pharmago-pharmacy-service:latest .
minikube image load pharmago-pharmacy-service:latest

echo "  - pharmago-api-gateway..."
docker build -f PharmaGo.ApiGateway/Dockerfile -t pharmago-api-gateway:latest .
minikube image load pharmago-api-gateway:latest

echo ""
echo "Construyendo imagen de frontend..."
cd "$CODE_DIR/Frontend"

echo "  - pharmago-ui..."
docker build -f Dockerfile -t pharmago-ui:latest .
minikube image load pharmago-ui:latest

echo ""
echo "=== Imágenes construidas y cargadas exitosamente ==="
echo ""
echo "Verificando imágenes en Minikube..."
minikube image ls | grep pharmago

cd "$SCRIPT_DIR"

