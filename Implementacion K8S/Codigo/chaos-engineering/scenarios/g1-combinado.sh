#!/bin/bash
# G1 - Escenario combinado: tráfico sostenido + CPU saturada en una réplica del gateway + latencia/pérdida de red
# en users-service, todo a la vez. Comprueba que las protecciones aguantan cuando falla más de una cosa.
# Uso: ./g1-combinado.sh [--secs 300] [--url URL] [--no-alerts]
source "$(dirname "${BASH_SOURCE[0]}")/../lib/common.sh"
scenario_init G1 "Combinado: tráfico + CPU + latencia de red" 300 "$@"

hypothesis "Con tráfico sostenido, una réplica del gateway sin CPU y users-service con 500ms de latencia y 20% de pérdida, el sistema degrada pero sigue sirviendo (>= 95%) y se recupera solo."
expect_alert "CPU de pod cerca del límite"

steady_state
restarts_snapshot
GW_POD="$(pick_pod "$GATEWAY" random)" || die "No hay pods del gateway."
U_PODS="$(pods_of "$USERS")"
net_preflight "$(echo $U_PODS | awk '{print $1}')" || die "No se pudo usar el contenedor efímero de red."
APPLY='tc qdisc replace dev $IF root netem delay 500ms 100ms loss 20%'
PARAMS+=("CPU: 2 bucles en $GW_POD" "red: netem 500ms/20% en $(echo $U_PODS)" "tráfico: 10 clientes concurrentes sobre /api/drug y /api/pharmacy")

probe_start
phase antes; hold "$PRE_SECS"

for p in $U_PODS; do net_inject "$p" "$APPLY" "$SECS"; done
phase durante
inject_cpu "$GW_POD" "$SECS" 2
LOAD="$WORK/load.txt"; : > "$LOAD"
( load_run GET /api/drug 5 "$SECS" "$LOAD" & load_run GET /api/pharmacy 5 "$SECS" "$LOAD" & wait ) &
LOAD_PID=$!
hold "$SECS"
wait "$LOAD_PID" 2>/dev/null

log "Rollback: CPU, red y tráfico"
kill_burn "$GW_POD"
for p in $U_PODS; do net_revert "$p"; done
phase despues; hold "$POST_SECS"
probe_stop

read -r n ok q l f t p95 <<<"$(load_summary "$LOAD")"
PARAMS+=("tráfico inyectado: $n requests, 2xx=$ok, 429=$l, 5xx=$f, timeouts=$t, p95=${p95}ms")
assert_avail durante 95
assert_avail despues 100
assert_latency_up "$USERS_PATHS" 2 p50
restarts_delta >/dev/null
[ "$RESTART_DELTA_TOTAL" -gt 0 ] && note "Reinicios durante el escenario combinado: $(echo "$RESTART_DELTA_DETAIL" | tr '\n' ';')"
assert_true "Todos los deployments Ready al terminar" "$(wait_ready 180 && echo 0 || echo 1)"
finish
