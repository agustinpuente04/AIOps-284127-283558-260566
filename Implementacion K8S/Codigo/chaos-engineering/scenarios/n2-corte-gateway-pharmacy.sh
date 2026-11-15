#!/bin/bash
# N2 - Interrupción de red total: corta el tráfico del API gateway hacia pharmacy-service (iptables en el pod).
# Los pods de pharmacy siguen vivos y Ready: solo el camino de red está roto.
# Uso: ./n2-corte-gateway-pharmacy.sh [--secs 240] [--mode drop|reject] [--url URL] [--no-alerts]
#   drop    (default) descarta los paquetes: las conexiones se cuelgan hasta el timeout (blackhole)
#   reject  responde con RST: falla rápido
source "$(dirname "${BASH_SOURCE[0]}")/../lib/common.sh"
scenario_init N2 "Corte de red gateway -> pharmacy-service" 240 "$@"
MODE="$(arg_val --mode drop)"
case "$MODE" in drop|reject) ;; *) die "--mode debe ser drop o reject";; esac

hypothesis "Con el camino gateway -> pharmacy cortado, /api/drug y /api/pharmacy fallan (5xx/timeout) mientras /api/users y la UI siguen disponibles; al restaurar la red el servicio se recupera solo."
[ "$MODE" = drop ] && expect_alert "Saturación de requests en curso"

steady_state
restarts_snapshot
PODS="$(pods_of "$GATEWAY")"; [ -n "$PODS" ] || die "No hay pods del gateway."
IP="$(svc_ip "$PHARMACY")"; [ -n "$IP" ] || die "No se obtuvo la ClusterIP de pharmacy."
net_preflight "$(echo $PODS | awk '{print $1}')" || die "No se pudo usar el contenedor efímero de red (¿acceso a Docker Hub? ¿kubectl >= 1.27?)."
if [ "$MODE" = drop ]; then
  APPLY='$IPT -A OUTPUT -d '"$IP"' -m comment --comment chaos-pharmago -j DROP'
else
  APPLY='$IPT -A OUTPUT -d '"$IP"' -p tcp -m comment --comment chaos-pharmago -j REJECT --reject-with tcp-reset'
fi
PARAMS+=("pods afectados (egress): $(echo $PODS)" "destino bloqueado: pharmacy-service ($IP), modo $MODE")
# La alerta de saturación (requests en vuelo > 20 por réplica durante 2 min) necesita tráfico real: el probe solo
# manda ~1 request/s y corta a los 5 s, así que deja como mucho 1 request en vuelo. En modo drop se agregan clientes
# autenticados (cubo propio, no se limitan con el probe) cuyas requests quedan colgadas hasta su timeout de 10 s.
CLIENTS="$(arg_val --clients 60)"
if [ "$MODE" = drop ]; then
  TOKEN="$(owner_token)"; [ -n "$TOKEN" ] || die "No se pudo iniciar sesión con el usuario de pruebas (define LOGIN_USER / LOGIN_PASS)."
  PARAMS+=("tráfico durante el corte: $CLIENTS clientes autenticados sobre /api/drug, generados desde el nodo")
fi

probe_start
phase antes; hold "$PRE_SECS"

for p in $PODS; do net_inject "$p" "$APPLY" "$SECS"; done
phase durante
if [ "$MODE" = drop ]; then
  LOAD="$WORK/load.txt"; : > "$LOAD"
  load_node /api/drug "$CLIENTS" "$SECS" "$LOAD" 0 ui "$TOKEN" &
  LOAD_PID=$!
fi
hold "$SECS"
READY_DURING="$(deploys_not_ready)"
if [ "$MODE" = drop ]; then
  INFLIGHT="$(prom_scalar 'max(max_over_time(pharmago_http_requests_in_flight{job="otlp-collector",exported_job="PharmaGo.ApiGateway"}['"$SECS"'s]))')"
  wait "$LOAD_PID" 2>/dev/null
  read -r n ok q l f t p95 <<<"$(load_summary "$LOAD")"
  PARAMS+=("tráfico inyectado: $n requests, 2xx=$ok, 429=$l, 5xx=$f, timeouts=$t" "pico de requests en vuelo en una réplica del gateway: ${INFLIGHT:-?}")
fi

log "Rollback: quitando la regla de iptables"
for p in $PODS; do net_revert "$p"; done
phase despues
MARK="$(phase_mark_ms despues)"
hold "$POST_SECS"
probe_stop

MTTR="$(mttr_since "$MARK" "$PHARMACY_PATHS")"
assert_5xx durante "$PHARMACY_PATHS"
assert_no_5xx durante "$USERS_PATHS"
assert_no_5xx durante '^/$'
assert_true "Recuperación automática tras restaurar la red (MTTR <= 120s)" "$([ "$MTTR" != n/a ] && awk -v m="$MTTR" 'BEGIN{exit !(m<=120)}' && echo 0 || echo 1)" "MTTR=${MTTR}s"
assert_avail despues 100
restarts_delta >/dev/null
assert_eq "Pods reiniciados durante el corte" "$RESTART_DELTA_TOTAL" 0
[ -z "$READY_DURING" ] && note "pharmacy-service seguía Ready durante el corte: ni las probes ni el Service detectan un problema de red entre el gateway y pharmacy; solo se ve en los 5xx/latencia."
finish
