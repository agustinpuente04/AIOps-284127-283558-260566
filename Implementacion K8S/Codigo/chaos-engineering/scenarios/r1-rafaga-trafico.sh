#!/bin/bash
# R1 - Inyección de requests: ráfaga de tráfico con concurrencia creciente (10 -> 50 -> 200).
# Uso: ./r1-rafaga-trafico.sh [--secs 180] [--direct] [--from-client] [--url URL] [--no-alerts]
#   --direct       salta nginx y ataca el gateway con X-Real-IP y X-Forwarded-For falsificados y rotativos.
#                  Verifica que ya NO se puede eludir el límite cambiando esas cabeceras.
#   --from-client  genera la carga desde esta máquina. Por defecto, si el acceso a la UI es por el túnel de
#                  `minikube service` (driver Docker en Windows/macOS), la carga se genera DENTRO del nodo,
#                  porque el túnel se satura con ~100 conexiones y daría timeouts que no son de la aplicación.
source "$(dirname "${BASH_SOURCE[0]}")/../lib/common.sh"
scenario_init R1 "Ráfaga de tráfico (inyección de requests)" 180 "$@"

DIRECT=0; has_arg --direct && DIRECT=1
FROM_NODE=0
# URL en 127.0.0.1/localhost = túnel o port-forward del host (no es la IP real del nodo)
if ! has_arg --from-client && [[ "$URL" =~ ^http://(127\.0\.0\.1|localhost) ]] && node_ssh_ok; then FROM_NODE=1; fi
if [ "$FROM_NODE" = 0 ] && [ "$DIRECT" = 1 ]; then
  pf_start gw "$GATEWAY" 80 || die "No se pudo abrir el port-forward al gateway."
  BASE="http://127.0.0.1:${_PF_PORT[gw]}"
else
  BASE="$URL"
fi
STEP=$(( SECS / 3 )); [ "$STEP" -lt 5 ] && STEP=5
GEN="$([ "$FROM_NODE" = 1 ] && echo 'dentro del nodo de Minikube' || echo 'desde esta máquina')"
PARAMS+=("generador de carga: $GEN" "destino: $([ "$DIRECT" = 1 ] && echo 'gateway con X-Real-IP / X-Forwarded-For rotativos (intento de eludir el límite)' || echo 'UI (nginx -> gateway)')" \
         "escalones de concurrencia: 10 -> 50 -> 200, ${STEP}s cada uno" "endpoints: GET /api/drug y GET /api/pharmacy")

if [ "$DIRECT" = 0 ]; then
  hypothesis "El token bucket anónimo absorbe la ráfaga: responde 429 (con Retry-After) pero nunca 5xx, el gateway no se reinicia y no queda ningún bloqueo residual al terminar."
else
  hypothesis "Rotar X-Real-IP / X-Forwarded-For ya no elude el límite: el gateway ignora esas cabeceras fuera de los pods de nginx, así que toda la ráfaga cuenta contra la misma IP."
fi
# 'Tráfico anómalo' es un z-score contra la media y desviación de la ÚLTIMA HORA: si esa hora ya contiene una
# ráfaga (p. ej. otra corrida de R1), la desviación está inflada y una ráfaga idéntica no llega a z > 3.
PEAK="$(prom_scalar 'max_over_time(sum(rate(pharmago_http_requests_total{job="otlp-collector", exported_job="PharmaGo.ApiGateway", endpoint!~"/health|/metrics"}[5m]))[1h:1m])')"
if [ -n "$PEAK" ] && awk -v p="$PEAK" 'BEGIN{exit !(p > 20)}'; then
  skip "Alerta 'Tráfico anómalo' disparada" "la última hora ya contiene una ráfaga (pico ${PEAK%.*} req/s) que infla la línea base del z-score; espera 1 h entre corridas de R1"
  note "La alerta 'Tráfico anómalo' usa un z-score sobre la última hora: una segunda ráfaga dentro de esa hora queda enmascarada por la primera (medido: misma carga de 135 req/s dio z=5.6 la primera vez y z=2.4 la segunda)."
else
  expect_alert "Tráfico anómalo"
fi

steady_state
restarts_snapshot
probe_start
phase antes; hold "$PRE_SECS"

phase durante
TOTAL5XX=0; TOTALTMO=0; TOTAL2XX=0; TOTAL429=0; TOTALREQ=0
for C in 10 50 200; do
  OUT="$WORK/step-$C.txt"; : > "$OUT"
  log "Escalón: $C clientes concurrentes durante ${STEP}s"
  half=$(( C / 2 ))
  if [ "$FROM_NODE" = 1 ]; then
    TARGET=ui; [ "$DIRECT" = 1 ] && TARGET=gateway
    load_node /api/drug     "$half" "$STEP" "$OUT" "$DIRECT" "$TARGET" & P1=$!
    load_node /api/pharmacy "$half" "$STEP" "$OUT" "$DIRECT" "$TARGET" & P2=$!
  else
    load_run GET /api/drug     "$half" "$STEP" "$OUT" "" "$DIRECT" "$BASE" & P1=$!
    load_run GET /api/pharmacy "$half" "$STEP" "$OUT" "" "$DIRECT" "$BASE" & P2=$!
  fi
  wait "$P1" "$P2"
  read -r n ok q l f t p95 <<<"$(load_summary "$OUT")"
  log "  c=$C: $n requests | 2xx=$ok 4xx=$q 429=$l 5xx=$f timeouts=$t | p95=${p95}ms"
  PARAMS+=("resultado c=$C: $n requests, 2xx=$ok, 429=$l, 5xx=$f, timeouts=$t, p95=${p95}ms")
  TOTAL5XX=$(( TOTAL5XX + f )); TOTALTMO=$(( TOTALTMO + t )); TOTAL2XX=$(( TOTAL2XX + ok )); TOTAL429=$(( TOTAL429 + l )); TOTALREQ=$(( TOTALREQ + n ))
  if alerts_active; then alerts_poll; fi
done
hold 5

phase despues; hold "$POST_SECS"
probe_stop

# Cota teórica de lo que puede pasar: cada réplica tiene su cubo (capacidad 100 + 100/min durante toda la ráfaga) y se
# deja un 25% de margen. Dos cubos anónimos por pod hacen que el total permitido sea ~2x el de una sola réplica.
REPLICAS="$(kubectl get deploy "$GATEWAY" -n "$NS" -o jsonpath='{.spec.replicas}')"
CAP="$(( 100 + 100 * (3 * STEP) / 60 ))"
BOUND="$(( REPLICAS * CAP * 125 / 100 ))"
PCT429=0; [ "$TOTALREQ" -gt 0 ] && PCT429=$(( TOTAL429 * 100 / TOTALREQ ))
PARAMS+=("cota de requests 2xx permitida: $BOUND ($REPLICAS réplicas x (capacidad 100 + 100/min x $((3 * STEP))s) + 25%)")

assert_eq "Ráfaga: respuestas 5xx del servidor" "$TOTAL5XX" 0
assert_true "El límite se cumple: 2xx <= cota teórica del token bucket" "$([ "$TOTAL2XX" -le "$BOUND" ] && echo 0 || echo 1)" "2xx=$TOTAL2XX, cota=$BOUND, 429=$TOTAL429 de $TOTALREQ"
assert_true "La ráfaga fue frenada (la gran mayoría recibió 429)" "$([ "$PCT429" -ge 90 ] && echo 0 || echo 1)" "${PCT429}% con 429"
if [ "$FROM_NODE" = 1 ]; then
  assert_eq "Ráfaga: timeouts de conexión" "$TOTALTMO" 0
  assert_avail durante 99
else
  skip "Timeouts y disponibilidad bajo la ráfaga" "el generador local satura el túnel del host ($TOTALTMO timeouts no atribuibles a la app); usa el modo por defecto (carga desde el nodo)"
fi
# Sin tope horario ni bloqueo residual: al terminar la ráfaga el tráfico normal vuelve a pasar en la fase 'despues'
assert_avail despues 100
restarts_delta "$GATEWAY" >/dev/null
assert_eq "Reinicios del gateway durante la ráfaga" "$RESTART_DELTA_TOTAL" 0
finish
