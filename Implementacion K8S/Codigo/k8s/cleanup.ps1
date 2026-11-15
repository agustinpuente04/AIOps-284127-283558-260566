# Script para limpiar/eliminar todos los recursos de PharmaGo en Kubernetes
# Equivalente a: docker-compose down
# Uso: .\cleanup.ps1

param(
    [switch]$KeepVolumes = $false
)

Write-Host "=== Limpiando recursos de PharmaGo en Kubernetes ===" -ForegroundColor Yellow

# Verificar que kubectl está disponible
if (-not (Get-Command kubectl -ErrorAction SilentlyContinue)) {
    Write-Host "Error: kubectl no está instalado o no está en el PATH" -ForegroundColor Red
    exit 1
}

# Opción 1: Eliminar namespace completo (más simple, equivalente a docker-compose down)
Write-Host "`nEliminando namespace 'pharmago' (esto elimina todos los recursos)..." -ForegroundColor Cyan
kubectl delete namespace pharmago --ignore-not-found=true

if ($LASTEXITCODE -eq 0) {
    Write-Host "Namespace eliminado. Esperando confirmación..." -ForegroundColor Green
    Start-Sleep -Seconds 3
} else {
    Write-Host "Advertencia: Puede que el namespace no exista" -ForegroundColor Yellow
}

# Eliminar PersistentVolumes (están fuera del namespace)
if (-not $KeepVolumes) {
    Write-Host "`nEliminando PersistentVolumes..." -ForegroundColor Cyan
    kubectl delete pv sql-pv elasticsearch-pv prometheus-pv grafana-pv --ignore-not-found=true
    Write-Host "PersistentVolumes eliminados" -ForegroundColor Green
} else {
    Write-Host "`nPersistentVolumes conservados (usar -KeepVolumes para conservarlos)" -ForegroundColor Yellow
}

Write-Host "`n=== Limpieza completada ===" -ForegroundColor Green
Write-Host "`nPara verificar que todo fue eliminado:" -ForegroundColor Cyan
Write-Host "  kubectl get all -n pharmago" -ForegroundColor White
Write-Host "  kubectl get pv" -ForegroundColor White

