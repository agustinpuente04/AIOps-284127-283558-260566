#!/bin/bash
# C1 - Sobrecarga de CPU en UNA réplica del API gateway (la otra debe sostener el servicio).
# Uso: ./c1-cpu-replica-gateway.sh [--secs 600] [--workers 2] [--url URL] [--no-alerts]
source "$(dirname "${BASH_SOURCE[0]}")/../lib/common.sh"
scenario_init C1 "CPU al 100% en una réplica del gateway" 600 "$@"
W="$(arg_val --workers 2)"

hypothesis "El límite de 500m frena (throttling) la réplica afectada, la otra sostiene el servicio (disponibilidad >= 99%) y salta la alerta de CPU cerca del límite."
expect_alert "CPU de pod cerca del límite"

steady_state
restarts_snapshot
POD="$(pick_pod "$GATEWAY" random)" || die "No hay pods del gateway."
PARAMS+=("pod afectado: $POD" "bucles de CPU: $W")
probe_start
phase antes; hold "$PRE_SECS"

phase durante
inject_cpu "$POD" "$SECS" "$W"
hold "$SECS"

log "Rollback: deteniendo bucles de CPU"
kill_burn "$POD"
phase despues; hold "$POST_SECS"
probe_stop

assert_avail durante 99
assert_avail despues 100
restarts_delta "$GATEWAY" >/dev/null; D="$RESTART_DELTA_DETAIL"
[ "$RESTART_DELTA_TOTAL" -gt 0 ] && note "La CPU saturada provocó reinicios por liveness probe: $D"
assert_true "El gateway terminó con todas sus réplicas Ready" "$(wait_deploy_ready "$GATEWAY" 120s && echo 0 || echo 1)"
finish
