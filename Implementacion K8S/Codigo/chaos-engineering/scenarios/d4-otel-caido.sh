#!/bin/bash
# D4 - Desconexión del OTel Collector: la observabilidad cae pero los usuarios no deberían notarlo.
# También verifica el efecto colateral: mientras está caído, las métricas pharmago_* dejan de llegar a Prometheus
# y las alertas que dependen de ellas quedan ciegas (noDataState: OK).
# Uso: ./d4-otel-caido.sh [--secs 180] [--url URL]
source "$(dirname "${BASH_SOURCE[0]}")/../lib/common.sh"
scenario_init D4 "Caída del OTel Collector" 180 "$@"
COLLECTOR=otlp-collector

hypothesis "Sin el OTel Collector los usuarios no notan nada (100%, latencia igual), pero Prometheus pierde las métricas de aplicación y sus alertas quedan ciegas."

steady_state
restarts_snapshot
probe_start
phase antes; hold "$PRE_SECS"

phase durante
on_cleanup "scale_restore $COLLECTOR"
scale_to "$COLLECTOR" 0
log "$COLLECTOR escalado a 0"
hold "$(( SECS - 20 ))"

# Con el collector caído, Prometheus ya debería ver up=0 en su target
UP="n/a"
if pf_start prom prometheus 9090; then
  UP="$(curl -s --max-time 5 "http://127.0.0.1:${_PF_PORT[prom]}/api/v1/query?query=up%7Bjob%3D%22otlp-collector%22%7D" | sed -n 's/.*"value":\[[^,]*,"\([01]\)".*/\1/p')"
fi
hold 20

log "Rollback: restaurando el collector"
scale_restore "$COLLECTOR"
phase despues; hold "$POST_SECS"
probe_stop

assert_avail durante 100
assert_latency_flat . 2 p95
assert_eq "Prometheus ve el target otlp-collector caído (up=0)" "$UP" 0
restarts_delta >/dev/null
assert_eq "Pods de la app reiniciados" "$RESTART_DELTA_TOTAL" 0
assert_true "El collector volvió a estar Ready" "$(wait_deploy_ready "$COLLECTOR" 120s && echo 0 || echo 1)"
note "Mientras el collector está caído no llegan las métricas pharmago_* (trafico, latencia, logins, circuit breaker): esas alertas usan noDataState=OK y no avisan de su propia ceguera. Falta una alerta sobre up{job=\"otlp-collector\"}."
finish
