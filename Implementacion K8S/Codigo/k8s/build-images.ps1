# Script para construir y cargar imágenes Docker en Minikube (multi-nodo)
# Funciona en Windows PowerShell
# Uso: .\build-images.ps1

Write-Host "=== Construyendo y cargando imágenes Docker en Minikube ===" -ForegroundColor Green

# Verificar que docker está disponible
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Write-Host "Error: Docker no está instalado o no está en el PATH" -ForegroundColor Red
    exit 1
}

# Verificar que minikube está corriendo
$minikubeStatus = minikube status 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "Error: Minikube no está corriendo. Ejecuta: minikube start --nodes 3" -ForegroundColor Red
    exit 1
}

# Obtener el directorio base (donde está este script)
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$codeDir = Split-Path -Parent $scriptDir

Write-Host "`nConstruyendo imágenes de backend..." -ForegroundColor Yellow
Set-Location "$codeDir\Backend"

Write-Host "  - pharmago-users-service..." -ForegroundColor Cyan
docker build -f PharmaGo.UsersService/Dockerfile -t pharmago-users-service:latest .
if ($LASTEXITCODE -ne 0) { 
    Write-Host "Error construyendo pharmago-users-service" -ForegroundColor Red
    exit 1 
}
minikube image load pharmago-users-service:latest

Write-Host "  - pharmago-pharmacy-service..." -ForegroundColor Cyan
docker build -f PharmaGo.PharmacyService/Dockerfile -t pharmago-pharmacy-service:latest .
if ($LASTEXITCODE -ne 0) { 
    Write-Host "Error construyendo pharmago-pharmacy-service" -ForegroundColor Red
    exit 1 
}
minikube image load pharmago-pharmacy-service:latest

Write-Host "  - pharmago-api-gateway..." -ForegroundColor Cyan
docker build -f PharmaGo.ApiGateway/Dockerfile -t pharmago-api-gateway:latest .
if ($LASTEXITCODE -ne 0) { 
    Write-Host "Error construyendo pharmago-api-gateway" -ForegroundColor Red
    exit 1 
}
minikube image load pharmago-api-gateway:latest

Write-Host "`nConstruyendo imagen de frontend..." -ForegroundColor Yellow
Set-Location "$codeDir\Frontend"

Write-Host "  - pharmago-ui..." -ForegroundColor Cyan
docker build -f Dockerfile -t pharmago-ui:latest .
if ($LASTEXITCODE -ne 0) { 
    Write-Host "Error construyendo pharmago-ui" -ForegroundColor Red
    exit 1 
}
minikube image load pharmago-ui:latest

Write-Host "`n=== Imágenes construidas y cargadas exitosamente ===" -ForegroundColor Green
Write-Host "`nVerificando imágenes en Minikube..." -ForegroundColor Yellow
minikube image ls | Select-String "pharmago"

Set-Location $scriptDir

