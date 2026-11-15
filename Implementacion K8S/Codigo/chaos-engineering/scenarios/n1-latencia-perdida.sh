#!/bin/bash
# N1 - Interrupción de red parcial: latencia + pérdida de paquetes (tc netem) en TODAS las réplicas de users-service.
# Usa un contenedor efímero con NET_ADMIN (kubectl debug --profile=netadmin); no modifica los Deployments.
# Uso: ./n1-latencia-perdida.sh [--secs 300] [--delay 500] [--loss 20] [--url URL] [--no-alerts]
source "$(dirname "${BASH_SOURCE[0]}")/../lib/common.sh"
scenario_init N1 "Latencia y pérdida de paquetes en users-service" 300 "$@"
DELAY="$(arg_val --delay 500)"; LOSS="$(arg_val --loss 20)"

hypothesis "Con ${DELAY}ms de latencia y ${LOSS}% de pérdida de paquetes en users-service, sus rutas se enlentecen (los retries de TCP actúan) pero no hay caída total; las rutas de pharmacy no se ven afectadas."
forbid_alert "Latencia anómala" "mide el tiempo de proceso dentro del servidor; el retardo de red ocurre al enviar y no lo ve"

steady_state
restarts_snapshot
PODS="$(pods_of "$USERS")"; [ -n "$PODS" ] || die "No hay pods de users-service."
net_preflight "$(echo $PODS | awk '{print $1}')" || die "No se pudo usar el contenedor efímero de red (¿acceso a Docker Hub? ¿kubectl >= 1.27?)."
APPLY="tc qdisc replace dev \$IF root netem delay ${DELAY}ms $(( DELAY / 5 ))ms loss ${LOSS}%"
PARAMS+=("pods afectados: $(echo $PODS)" "netem: delay ${DELAY}ms ±$(( DELAY / 5 ))ms, loss ${LOSS}% (egress de los pods)")

probe_start
phase antes; hold "$PRE_SECS"

for p in $PODS; do net_inject "$p" "$APPLY" "$SECS"; done
phase durante
hold "$SECS"

log "Rollback: quitando tc netem"
for p in $PODS; do net_revert "$p"; done
phase despues; hold "$POST_SECS"
probe_stop

assert_latency_up "$USERS_PATHS" 2 p50
assert_latency_flat "$PHARMACY_PATHS" 3 p95
assert_avail durante 90
assert_avail despues 100
restarts_delta "$USERS" >/dev/null; D="$RESTART_DELTA_DETAIL"
[ "$RESTART_DELTA_TOTAL" -gt 0 ] && note "La pérdida de paquetes hizo fallar probes de users-service y provocó reinicios: $D"
note "La alerta 'Latencia anómala' es ciega a la latencia de red: pharmago_http_request_duration se mide en el middleware del servidor, antes de que el retardo/pérdida de tc netem afecte a los paquetes. Con ${DELAY}ms de retardo los clientes vieron la latencia multiplicarse mientras la métrica del servidor siguió en su línea base. Para detectar degradación de red hay que medir desde el cliente (gateway -> servicio, p. ej. http_client_duration) o con probes externos."
assert_true "users-service terminó con todas sus réplicas Ready" "$(wait_deploy_ready "$USERS" 120s && echo 0 || echo 1)"
finish
