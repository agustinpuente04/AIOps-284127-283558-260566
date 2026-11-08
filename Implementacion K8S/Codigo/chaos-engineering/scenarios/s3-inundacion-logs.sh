#!/bin/bash
# S3 - Sobrecarga de storage por logs: un pod de users-service escribe miles de líneas por segundo en stdout.
# Fluent-bit las recoge y Elasticsearch las indexa. Se mide cuánto llega y cuánto crece el índice.
# Rollback: se borran del índice los documentos con la marca CHAOS-LOG-FLOOD.
# Uso: ./s3-inundacion-logs.sh [--secs 180] [--rate 200] [--url URL] [--no-alerts]
source "$(dirname "${BASH_SOURCE[0]}")/../lib/common.sh"
scenario_init S3 "Inundación de logs (Fluent-bit -> Elasticsearch)" 180 "$@"
RATE="$(arg_val --rate 200)"
MARK="CHAOS-LOG-FLOOD"
QRY='q=%22CHAOS-LOG-FLOOD%22'

es() { kubectl exec -n "$NS" deploy/elasticsearch -c elasticsearch -- curl -s "$@"; }
es_bytes() { es 'localhost:9200/_cat/indices/pharmago-logs-*?h=store.size&bytes=b' | awk '{ s += $1 } END { printf "%d", s }'; }
es_count() { es "localhost:9200/pharmago-logs-*/_count?$QRY" | sed -n 's/.*"count":\([0-9]*\).*/\1/p'; }
es_clean() { es -X POST "localhost:9200/pharmago-logs-*/_delete_by_query?$QRY&conflicts=proceed&refresh=true"; }

hypothesis "La inundación de logs no afecta a los usuarios (>= 99%); la tubería Fluent-bit -> Elasticsearch la absorbe sin perder más de la mitad de las líneas."

steady_state
restarts_snapshot
es 'localhost:9200/_cluster/health' | grep -q '"status"' || die "Elasticsearch no responde."
POD="$(pick_pod "$USERS" random)" || die "No hay pods de users-service."
BYTES0="$(es_bytes)"; COUNT0="$(es_count)"
PARAMS+=("pod emisor: $POD" "tasa: $RATE líneas/s durante ${SECS}s (~$(( RATE * SECS )) líneas)" "tamaño de los índices antes: $(( BYTES0 / 1048576 ))MB")
probe_start
phase antes; hold "$PRE_SECS"

phase durante
on_cleanup es_clean
PAD="$(head -c 150 /dev/zero | tr '\0' 'x')"
kubectl exec -n "$NS" "$POD" -- sh -c "CHAOS_BURN=1 timeout $SECS sh -c 'i=0; while :; do j=0; while [ \$j -lt $RATE ]; do echo \"$MARK seq=\$i.\$j $PAD\"; j=\$((j+1)); done; i=\$((i+1)); sleep 1; done' >> /proc/1/fd/1" >/dev/null 2>&1 &
on_cleanup "kill_burn $POD"
log "Inundando logs: $RATE líneas/s desde $POD"
hold "$SECS"
kill_burn "$POD"

log "Esperando 30s a que Fluent-bit vacíe sus buffers..."
sleep 30
BYTES1="$(es_bytes)"; COUNT1="$(es_count)"
SENT=$(( RATE * SECS )); RECV=$(( ${COUNT1:-0} - ${COUNT0:-0} ))
PCT=$(( RECV * 100 / (SENT > 0 ? SENT : 1) ))
PARAMS+=("líneas enviadas: ~$SENT, indexadas con la marca: $RECV (${PCT}%)" "crecimiento de los índices: $(( (BYTES1 - BYTES0) / 1048576 ))MB")
log "Líneas enviadas ~$SENT, indexadas $RECV (${PCT}%). Índices +$(( (BYTES1 - BYTES0) / 1048576 ))MB"

log "Rollback: borrando los documentos de la inundación"
es_clean >/dev/null
phase despues; hold "$POST_SECS"
probe_stop

assert_true "Al menos el 50% de las líneas llegó a Elasticsearch" "$([ "$PCT" -ge 50 ] && echo 0 || echo 1)" "${PCT}% (${RECV} de ~${SENT})"
assert_avail durante 99
assert_avail despues 100
restarts_delta >/dev/null
assert_eq "Pods reiniciados durante la inundación" "$RESTART_DELTA_TOTAL" 0
assert_true "Elasticsearch sigue sano (cluster health responde)" "$(es 'localhost:9200/_cluster/health' | grep -q '"status":"\(green\|yellow\)"' && echo 0 || echo 1)"
[ "$PCT" -lt 100 ] && note "Se perdió el $((100 - PCT))% de las líneas entre stdout del pod y Elasticsearch: la tubería de logs no garantiza entrega bajo ráfagas."
finish
