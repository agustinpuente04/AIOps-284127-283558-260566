#!/bin/bash
# D3 - Desconexión de la base de datos: escala pharmago-db a 0 y la vuelve a levantar.
# Los datos persisten en el PV (sql-pvc). Se mide si las apps se recuperan solas, sin reiniciarlas.
# Uso: ./d3-bd-caida.sh [--secs 90] [--url URL]
source "$(dirname "${BASH_SOURCE[0]}")/../lib/common.sh"
POST_DEFAULT=180      # SQL Server tarda ~1 min en volver a aceptar conexiones
scenario_init D3 "Caída de la base de datos" 90 "$@"

hypothesis "Con la BD caída los endpoints que la usan fallan (5xx) pero la UI sigue disponible; al volver la BD las apps se recuperan solas, sin reinicios, y los datos persisten."

steady_state
restarts_snapshot
DRUGS_BEFORE="$(curl -s --max-time 10 "$URL/api/drug" | tr -cd '{' | wc -c)"
probe_start
phase antes; hold "$PRE_SECS"

phase durante
on_cleanup "scale_restore $DB"
scale_to "$DB" 0
log "pharmago-db escalado a 0"
hold "$SECS"

log "Rollback: levantando la BD"
scale_restore "$DB"
phase despues
MARK="$(phase_mark_ms despues)"
hold "$POST_SECS"
probe_stop

MTTR="$(mttr_since "$MARK" "$DB_PATHS")"
DRUGS_AFTER="$(curl -s --max-time 10 "$URL/api/drug" | tr -cd '{' | wc -c)"
PARAMS+=("MTTR de los endpoints con BD tras levantarla: ${MTTR}s" "registros de /api/drug antes/después: $DRUGS_BEFORE / $DRUGS_AFTER")
assert_5xx durante "$DB_PATHS"
assert_no_5xx durante '^/$'
assert_true "Las apps se recuperan solas tras volver la BD (MTTR <= 150s)" "$([ "$MTTR" != n/a ] && awk -v m="$MTTR" 'BEGIN{exit !(m<=150)}' && echo 0 || echo 1)" "MTTR=${MTTR}s"
assert_eq "Los datos persisten tras reiniciar la BD (registros de /api/drug)" "$DRUGS_AFTER" "$DRUGS_BEFORE"
restarts_delta "pharmago-(api-gateway|users-service|pharmacy-service|ui)" >/dev/null
assert_eq "Reinicios de las apps (no deberían hacer falta)" "$RESTART_DELTA_TOTAL" 0
assert_true "pharmago-db terminó Ready" "$(wait_deploy_ready "$DB" 120s && echo 0 || echo 1)"
finish
