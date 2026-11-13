#!/bin/bash
# C3 - Sobrecarga de CPU en la base de datos (SQL Server, límite 1000m).
# Se observa cómo la lentitud de la BD se propaga a todos los servicios que dependen de ella.
# Uso: ./c3-cpu-db.sh [--secs 600] [--workers 4] [--url URL] [--no-alerts]
source "$(dirname "${BASH_SOURCE[0]}")/../lib/common.sh"
scenario_init C3 "CPU al 100% en pharmago-db" 600 "$@"
W="$(arg_val --workers 4)"

hypothesis "Con la CPU de SQL Server saturada las rutas con BD se degradan; la UI estática sigue disponible y no hay caída total."
# Hueco de cobertura conocido: el init container volume-permissions no declara limits, así que el POD no tiene cuota
# de CPU (container_spec_cpu_quota no existe para pharmago-db) y la regla no tiene contra qué comparar, aunque el
# contenedor de SQL Server esté al 100% de su límite. Afecta igual a elasticsearch, grafana y prometheus.
forbid_alert "CPU de pod cerca del límite" "el init container sin limits deja al pod sin cuota de CPU y la regla no puede calcular el %"

steady_state
restarts_snapshot
POD="$(pick_pod "$DB")" || die "No hay pod de la BD."
PARAMS+=("pod afectado: $POD" "bucles de CPU: $W")
probe_start
phase antes; hold "$PRE_SECS"

phase durante
inject_cpu "$POD" "$SECS" "$W"
hold $(( SECS / 2 ))
assert_cpu_saturated "$POD"
hold $(( SECS - SECS / 2 ))

log "Rollback: deteniendo bucles de CPU"
kill_burn "$POD"
phase despues; hold "$POST_SECS"
probe_stop

note_latency "$DB_PATHS" p95
assert_no_5xx durante '^/$'
assert_avail durante 95
assert_avail despues 100
restarts_delta "$DB" >/dev/null; D="$RESTART_DELTA_DETAIL"
[ "$RESTART_DELTA_TOTAL" -gt 0 ] && note "La BD se reinició bajo CPU saturada: $D"
finish
