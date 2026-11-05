#!/bin/bash
# Verifica que la app sigue disponible (100%) mientras se reemplazan sus componentes.
#
# Uso:
#   ./zero-downtime-test.sh <URL> [segundos] [--rollout]
#
#   URL        URL de la UI obtenida con: minikube service pharmago-ui -n pharmago --url
#              (NO usar port-forward: queda atado a un único pod y se corta al reemplazarlo)
#   segundos   Duración de la medición (default 180)
#   --rollout  Además ejecuta "kubectl rollout restart" de UI, gateway, users y pharmacy
#              y espera a que terminen, mientras sigue midiendo.
#
# Mientras corre, se pueden provocar fallas a mano en otra terminal, por ejemplo:
#   kubectl delete pod -n pharmago -l app=pharmago-api-gateway --wait=false   (con 2 réplicas, borrar UNA)
#
# Se cuenta como fallo: 000 (error de conexión/timeout) y 5xx.
# 4xx = el componente respondió (OK). 429 (rate limit) se reporta aparte.
# Sale con código 1 si hubo algún fallo.

URL="${1:?Uso: $0 <URL> [segundos] [--rollout]}"
URL="${URL%/}"
DURATION="${2:-180}"
ROLLOUT="${3:-}"
INTERVAL="${INTERVAL:-0.3}"   # ~3 req/s, por debajo del rate limit del gateway (100/min por réplica)
NS=pharmago
APPS="pharmago-ui pharmago-api-gateway pharmago-users-service pharmago-pharmacy-service"

# UI -> (nginx) -> gateway -> pharmacy -> BD  /  gateway -> users (401/403 = respondió)
ENDPOINTS=("/" "/api/drug" "/api/pharmacy" "/api/users")

total=0; ok=0; fail=0; limited=0

if [ "$ROLLOUT" = "--rollout" ]; then
  (
    sleep 5
    echo "[rollout] kubectl rollout restart de: $APPS"
    for app in $APPS; do kubectl rollout restart deployment/"$app" -n "$NS"; done
    for app in $APPS; do kubectl rollout status deployment/"$app" -n "$NS" --timeout=600s; done
    echo "[rollout] todos los rollouts completados"
  ) &
  ROLLOUT_PID=$!
fi

echo "Midiendo disponibilidad de $URL durante ${DURATION}s..."
end=$(( $(date +%s) + DURATION ))
i=0
while [ "$(date +%s)" -lt "$end" ]; do
  path="${ENDPOINTS[$(( i % ${#ENDPOINTS[@]} ))]}"
  code=$(curl -s -o /dev/null -w "%{http_code}" --max-time 5 "$URL$path")
  total=$((total+1))
  if [ "$code" = "000" ] || [ "$code" -ge 500 ]; then
    fail=$((fail+1))
    echo "$(date +%T) FALLO $code $path"
  else
    ok=$((ok+1))
    [ "$code" = "429" ] && limited=$((limited+1))
  fi
  i=$((i+1))
  sleep "$INTERVAL"
done

[ -n "$ROLLOUT_PID" ] && wait "$ROLLOUT_PID"

pct=$(awk -v ok="$ok" -v t="$total" 'BEGIN { if (t == 0) print "0.00"; else printf "%.2f", ok * 100 / t }')
echo ""
echo "=== Resultado ==="
echo "  Requests:       $total"
echo "  OK (<500):      $ok"
echo "  Fallos:         $fail"
echo "  Rate limited:   $limited (429, cuentan como disponibles)"
echo "  Disponibilidad: ${pct}%"

[ "$fail" -eq 0 ]
