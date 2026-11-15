#!/bin/bash
# D2 - Desconexión de un componente completo: escala pharmacy-service a 0 réplicas.
# Nota: el PodDisruptionBudget solo protege contra evictions/drain; NO impide escalar a 0.
# Rollback: restaura las réplicas originales (guardadas en una anotación, también usada por `chaos.sh cleanup`).
# Uso: ./d2-pharmacy-a-cero.sh [--secs 240] [--url URL] [--no-alerts]
source "$(dirname "${BASH_SOURCE[0]}")/../lib/common.sh"
scenario_init D2 "pharmacy-service escalado a 0 réplicas" 240 "$@"

hypothesis "Sin pharmacy-service fallan solo sus rutas (5xx); users y la UI siguen disponibles, salta la alerta de servicio sin réplicas y al restaurar el servicio se recupera solo."
expect_alert "Servicio sin réplicas disponibles"
forbid_alert "Circuit breaker abierto" "users-service no llama a pharmacy en estos flujos"

steady_state
restarts_snapshot
probe_start
phase antes; hold "$PRE_SECS"

phase durante
on_cleanup "scale_restore $PHARMACY"
scale_to "$PHARMACY" 0
log "pharmacy-service escalado a 0"
hold "$SECS"

log "Rollback: restaurando réplicas"
scale_restore "$PHARMACY"
phase despues
MARK="$(phase_mark_ms despues)"
wait_deploy_ready "$PHARMACY" 180s
hold "$POST_SECS"
probe_stop

MTTR="$(mttr_since "$MARK" "$PHARMACY_PATHS")"
PARAMS+=("MTTR de las rutas de pharmacy tras restaurar: ${MTTR}s")
assert_5xx durante "$PHARMACY_PATHS"
assert_no_5xx durante "$USERS_PATHS"
assert_no_5xx durante '^/$'
assert_true "Recuperación automática (MTTR <= 90s)" "$([ "$MTTR" != n/a ] && awk -v m="$MTTR" 'BEGIN{exit !(m<=90)}' && echo 0 || echo 1)" "MTTR=${MTTR}s"
assert_true "pharmacy-service terminó con todas sus réplicas Ready" "$(wait_deploy_ready "$PHARMACY" 120s && echo 0 || echo 1)"
finish
