#!/bin/bash
# M1 - Sobrecarga de memoria SIN superar el límite: lleva un pod de users-service al ~88% de limits.memory.
# Uso: ./m1-memoria-cerca-limite.sh [--secs 300] [--pct 88] [--url URL] [--no-alerts]
source "$(dirname "${BASH_SOURCE[0]}")/../lib/common.sh"
scenario_init M1 "Memoria cerca del límite sin OOMKill" 300 "$@"
PCT="$(arg_val --pct 88)"

hypothesis "El pod llega a ~${PCT}% de su límite de memoria, dispara la alerta de memoria, NO se reinicia y el servicio mantiene >= 99% de disponibilidad."
expect_alert "Memoria de pod cerca del límite"

steady_state
restarts_snapshot
POD="$(pick_pod "$USERS" random)" || die "No hay pods de users-service."
read -r WS LIM <<<"$(pod_mem "$POD")"
[ -n "$LIM" ] && [ "$LIM" -gt 0 ] 2>/dev/null || die "No se pudo leer el cgroup de $POD."
[ "$LIM" -gt 17179869184 ] && die "$POD no tiene límite de memoria; el escenario necesita limits.memory."
MB=$(( (LIM * PCT / 100 - WS) / 1048576 ))
[ "$MB" -ge 10 ] || die "$POD ya usa $((WS/1048576))Mi de $((LIM/1048576))Mi; no hay margen para llegar al ${PCT}%."
PARAMS+=("pod afectado: $POD" "working set inicial: $((WS/1048576))Mi de $((LIM/1048576))Mi" "memoria inyectada: ${MB}Mi (objetivo ${PCT}%)")
probe_start
phase antes; hold "$PRE_SECS"

phase durante
inject_mem "$POD" "$MB" "$SECS"
sleep 10
read -r WS2 _ <<<"$(pod_mem "$POD")"
log "Working set tras inyectar: $((WS2/1048576))Mi de $((LIM/1048576))Mi ($((WS2 * 100 / LIM))%)"
PARAMS+=("working set alcanzado: $((WS2/1048576))Mi ($((WS2 * 100 / LIM))%)")
hold "$SECS"

log "Rollback: liberando memoria"
kill_burn "$POD"
phase despues; hold "$POST_SECS"
probe_stop

assert_true "El working set llegó al umbral de alerta (>= 85%)" "$([ $((WS2 * 100 / LIM)) -ge 85 ] && echo 0 || echo 1)" "$((WS2 * 100 / LIM))%"
restarts_delta "$USERS" >/dev/null
assert_eq "Reinicios de users-service (sin OOMKill)" "$RESTART_DELTA_TOTAL" 0
assert_avail durante 99
assert_avail despues 100
finish
