#!/bin/bash
# N3 - Interrupción de red total: corta el tráfico de users-service y pharmacy-service hacia la BD (TCP 1433).
# La BD sigue viva; solo se rompe el camino de red. Verifica cómo se comportan las apps y sus probes.
# Uso: ./n3-corte-servicios-bd.sh [--secs 180] [--mode drop|reject] [--url URL] [--no-alerts]
source "$(dirname "${BASH_SOURCE[0]}")/../lib/common.sh"
scenario_init N3 "Corte de red servicios -> base de datos" 180 "$@"
MODE="$(arg_val --mode drop)"
case "$MODE" in drop|reject) ;; *) die "--mode debe ser drop o reject";; esac

hypothesis "Sin red hacia la BD, los endpoints que la usan fallan (5xx/timeout) pero la UI sigue disponible, los pods no se reinician y las apps se recuperan solas al restaurar la red."
expect_alert "Circuit breaker abierto"

steady_state
restarts_snapshot
PODS="$(pods_of "$USERS") $(pods_of "$PHARMACY")"
IP="$(svc_ip "$DB")"; [ -n "$IP" ] || die "No se obtuvo la ClusterIP de la BD."
net_preflight "$(echo $PODS | awk '{print $1}')" || die "No se pudo usar el contenedor efímero de red (¿acceso a Docker Hub? ¿kubectl >= 1.27?)."
if [ "$MODE" = drop ]; then
  APPLY='$IPT -A OUTPUT -d '"$IP"' -p tcp --dport 1433 -m comment --comment chaos-pharmago -j DROP'
else
  APPLY='$IPT -A OUTPUT -d '"$IP"' -p tcp --dport 1433 -m comment --comment chaos-pharmago -j REJECT --reject-with tcp-reset'
fi
PARAMS+=("pods afectados (egress): $(echo $PODS)" "destino bloqueado: $DB ($IP):1433, modo $MODE")

probe_start
phase antes; hold "$PRE_SECS"

for p in $PODS; do net_inject "$p" "$APPLY" "$SECS"; done
phase durante
hold "$SECS"
READY_DURING="$(deploys_not_ready)"

log "Rollback: quitando las reglas de iptables"
for p in $PODS; do net_revert "$p"; done
phase despues
MARK="$(phase_mark_ms despues)"
hold "$POST_SECS"
probe_stop

MTTR="$(mttr_since "$MARK" "$DB_PATHS")"
assert_5xx durante "$DB_PATHS"
assert_p50_below durante "$DB_PATHS" 2000
assert_no_5xx durante '^/$'
assert_true "Recuperación automática sin reiniciar servicios (MTTR <= 120s)" "$([ "$MTTR" != n/a ] && awk -v m="$MTTR" 'BEGIN{exit !(m<=120)}' && echo 0 || echo 1)" "MTTR=${MTTR}s"
restarts_delta >/dev/null
assert_eq "Pods reiniciados durante el corte" "$RESTART_DELTA_TOTAL" 0
assert_true "Los pods siguieron Ready durante el corte (el /health no verifica la BD)" "$([ -z "$READY_DURING" ] && echo 0 || echo 1)" "${READY_DURING:-todos Ready}"
note "Los pods de users y pharmacy siguen Ready con la BD inalcanzable porque /health usa AddHealthChecks() sin comprobar la BD: Kubernetes sigue enviándoles tráfico que falla con 5xx. Un readiness probe que consulte la BD (AddDbContextCheck) los sacaría del balanceo."
finish
