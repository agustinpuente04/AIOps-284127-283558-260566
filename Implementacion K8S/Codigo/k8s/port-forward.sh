#!/bin/bash
# Ejecuta todos los port-forwards en background (mismos puertos que docker-compose)
# Uso: ./port-forward.sh
# Para detener: ./port-forward.sh --stop
#
# Si 9200 está ocupado (otro kubectl, Docker ES, etc.): ES_LOCAL_PORT=19200 ./port-forward.sh
# Luego Elasticsearch queda en http://127.0.0.1:19200

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
PID_FILE="$SCRIPT_DIR/.port-forward.pids"

stop_forwards() {
    if [ -f "$PID_FILE" ]; then
        echo "Deteniendo port-forwards..."
        while read pid; do
            kill $pid 2>/dev/null && echo "  Detenido PID $pid" || true
        done < "$PID_FILE"
        rm -f "$PID_FILE"
        echo "Listo."
    else
        echo "No hay port-forwards en ejecución (o ya fueron detenidos)."
    fi
    exit 0
}

[ "$1" = "--stop" ] && stop_forwards

echo "=== Iniciando port-forwards en background ==="
echo ""

> "$PID_FILE"

# Frontend
kubectl port-forward svc/pharmago-ui 4200:80 -n pharmago &
echo $! >> "$PID_FILE"
echo "  Frontend:      http://127.0.0.1:4200"

# API Gateway
kubectl port-forward svc/pharmago-api-gateway 5000:80 -n pharmago &
echo $! >> "$PID_FILE"
echo "  API Gateway:  http://127.0.0.1:5000"

# Users Service
kubectl port-forward svc/pharmago-users-service 5001:80 -n pharmago &
echo $! >> "$PID_FILE"
echo "  Users:        http://127.0.0.1:5001"

# Pharmacy Service
kubectl port-forward svc/pharmago-pharmacy-service 5002:80 -n pharmago &
echo $! >> "$PID_FILE"
echo "  Pharmacy:     http://127.0.0.1:5002"

# SQL Server
kubectl port-forward svc/pharmago-db 11433:1433 -n pharmago &
echo $! >> "$PID_FILE"
echo "  BD:           127.0.0.1,11433 (sa / Str0ngP@ssword!)"

# Prometheus
kubectl port-forward svc/prometheus 9090:9090 -n pharmago &
echo $! >> "$PID_FILE"
echo "  Prometheus:   http://127.0.0.1:9090"

# Grafana
kubectl port-forward svc/grafana 3000:3000 -n pharmago &
echo $! >> "$PID_FILE"
echo "  Grafana:      http://127.0.0.1:3000 (admin/admin)"

# Kibana
kubectl port-forward svc/kibana 5601:5601 -n pharmago &
echo $! >> "$PID_FILE"
echo "  Kibana:       http://127.0.0.1:5601"

# Elasticsearch (puerto local configurable; en el cluster sigue siendo 9200)
ES_LOCAL_PORT="${ES_LOCAL_PORT:-9200}"
kubectl port-forward svc/elasticsearch "${ES_LOCAL_PORT}:9200" -n pharmago &
echo $! >> "$PID_FILE"
echo "  Elasticsearch: http://127.0.0.1:${ES_LOCAL_PORT}"

echo ""
echo "Port-forwards activos. Para detener: ./port-forward.sh --stop"
echo ""
