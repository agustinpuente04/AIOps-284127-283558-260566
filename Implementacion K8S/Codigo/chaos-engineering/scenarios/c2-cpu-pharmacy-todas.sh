#!/bin/bash
# C2 - Sobrecarga de CPU en TODAS las réplicas de pharmacy-service.
# El daño debe quedar acotado a las rutas de pharmacy; las de users no deben notarlo.
# Uso: ./c2-cpu-pharmacy-todas.sh [--secs 600] [--workers 4] [--url URL] [--no-alerts]
source "$(dirname "${BASH_SOURCE[0]}")/../lib/common.sh"
scenario_init C2 "CPU al 100% en todas las réplicas de pharmacy-service" 600 "$@"
W="$(arg_val --workers 4)"

hypothesis "Con la CPU de pharmacy saturada, /api/drug y /api/pharmacy se degradan sin caída total y /api/users no se ve afectado."
expect_alert "CPU de pod cerca del límite"

steady_state
restarts_snapshot
PODS="$(pods_of "$PHARMACY")"; [ -n "$PODS" ] || die "No hay pods de pharmacy."
PARAMS+=("pods afectados: $(echo $PODS)" "bucles de CPU por pod: $W")
probe_start
phase antes; hold "$PRE_SECS"

phase durante
for p in $PODS; do inject_cpu "$p" "$SECS" "$W"; done
hold $(( SECS / 2 ))
for p in $PODS; do assert_cpu_saturated "$p"; done
hold $(( SECS - SECS / 2 ))

log "Rollback: deteniendo bucles de CPU"
for p in $PODS; do kill_burn "$p"; done
phase despues; hold "$POST_SECS"
probe_stop

note_latency "$PHARMACY_PATHS" p95
assert_latency_flat "$USERS_PATHS" 3 p95
assert_avail durante 95
assert_avail despues 100
restarts_delta "$PHARMACY" >/dev/null; D="$RESTART_DELTA_DETAIL"
[ "$RESTART_DELTA_TOTAL" -gt 0 ] && note "pharmacy-service se reinició bajo CPU saturada: $D"
finish
