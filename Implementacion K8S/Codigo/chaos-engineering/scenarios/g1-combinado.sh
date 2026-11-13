#!/bin/bash
# G1 - Escenario combinado: tráfico sostenido + CPU saturada en una réplica del gateway + latencia/pérdida de red
# en users-service, todo a la vez. Comprueba que las protecciones aguantan cuando falla más de una cosa.
# Uso: ./g1-combinado.sh [--secs 300] [--url URL] [--no-alerts]
source "$(dirname "${BASH_SOURCE[0]}")/../lib/common.sh"
scenario_init G1 "Combinado: tráfico + CPU + latencia de red" 300 "$@"

hypothesis "Con tráfico sostenido, una réplica del gateway sin CPU y users-service con 500ms de latencia y 20% de pérdida, el sistema degrada pero sigue sirviendo (>= 95%) y se recupera solo."
# No se espera la alerta "CPU de pod cerca del límite": la regla usa rate[5m] + for 3m y tarda ~7 min en disparar
# (C1: 442 s), más que los 300 s de inyección de G1. La alerta se valida en C1, que dura 600 s.

steady_state
restarts_snapshot
GW_POD="$(pick_pod "$GATEWAY" random)" || die "No hay pods del gateway."
U_PODS="$(pods_of "$USERS")"
net_preflight "$(echo $U_PODS | awk '{print $1}')" || die "No se pudo usar el contenedor efímero de red."
TOKEN="$(owner_token)"; [ -n "$TOKEN" ] || die "No se pudo iniciar sesión con el usuario de pruebas (define LOGIN_USER / LOGIN_PASS)."
APPLY='tc qdisc replace dev $IF root netem delay 500ms 100ms loss 20%'
PARAMS+=("CPU: 2 bucles en $GW_POD" "red: netem 500ms/20% en $(echo $U_PODS)" "tráfico: 10 clientes autenticados concurrentes sobre /api/drug y /api/pharmacy, generados desde el nodo")

probe_start
phase antes; hold "$PRE_SECS"

for p in $U_PODS; do net_inject "$p" "$APPLY" "$SECS"; done
phase durante
inject_cpu "$GW_POD" "$SECS" 2
# Carga autenticada y desde el nodo: con carga anónima desde el host, el tráfico agotaba el cubo de la IP que comparte
# con el probe, el probe recibía 429 del gateway (114 de 131 en /api/users) y la latencia de users nunca se medía.
LOAD="$WORK/load.txt"; : > "$LOAD"
( load_node /api/drug 5 "$SECS" "$LOAD" 0 ui "$TOKEN" & load_node /api/pharmacy 5 "$SECS" "$LOAD" 0 ui "$TOKEN" & wait ) &
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
# Comportamiento del sistema, no del test (README, hallazgo 17): con users degradado el gateway no puede validar
# las sesiones, trata la carga autenticada como anónima y la descuenta del cubo de la IP que comparte con el probe.
PS=($(probe_stats durante .))
[ "${PS[2]:-0}" -gt 0 ] && note "El probe (anónimo) recibió ${PS[2]} respuestas 429 de ${PS[0]} durante la inyección: con users-service degradado el gateway no pudo validar las sesiones de la carga autenticada, la trató como anónima y agotó el cubo de la IP que comparte con el probe."
restarts_delta >/dev/null
[ "$RESTART_DELTA_TOTAL" -gt 0 ] && note "Reinicios durante el escenario combinado: $(echo "$RESTART_DELTA_DETAIL" | tr '\n' ';')"
assert_true "Todos los deployments Ready al terminar" "$(wait_ready 180 && echo 0 || echo 1)"
finish
