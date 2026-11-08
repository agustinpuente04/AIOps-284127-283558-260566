#!/bin/bash
# R4 - Aislamiento: una IP que inunda con tráfico anónimo NO debe dejar sin servicio a un usuario autenticado que
# comparte esa IP, y el bloqueo debe levantarse en segundos (no en una hora). Es la prueba directa del rate limit por
# token bucket con cubos separados por identidad.
#
# Todo el tráfico (inundación + las dos sondas) sale del MISMO sitio, el nodo de Minikube, para que compartan IP:
# desde el host el túnel de `minikube service` limita la concurrencia y desde otro origen serían IPs distintas.
#
# Uso: ./r4-aislamiento-por-ip.sh [--secs 90] [--url URL]
#   LOGIN_USER / LOGIN_PASS (por defecto admin / Abcdef12., usuario de la semilla)
source "$(dirname "${BASH_SOURCE[0]}")/../lib/common.sh"
scenario_init R4 "Aislamiento por identidad bajo inundación anónima" 90 "$@"
node_ssh_ok || die "R4 necesita 'minikube ssh' (la carga se genera dentro del nodo)."
OWNER="${LOGIN_USER:-admin}"; OWNER_PASS="${LOGIN_PASS:-Abcdef12.}"

hypothesis "Con la IP inundada de tráfico anónimo, el usuario autenticado de esa misma IP sigue recibiendo 200 (cubo propio), el anónimo recibe 429 con Retry-After y, al cesar la inundación, el anónimo se recupera en segundos."

steady_state
restarts_snapshot

TOKEN="$(curl -s --max-time 10 -X POST "$URL/api/login" -H 'Content-Type: application/json' \
         -d "{\"userName\":\"$OWNER\",\"password\":\"$OWNER_PASS\"}" | sed -n 's/.*"token":"\([^"]*\)".*/\1/p')"
[ -n "$TOKEN" ] || die "No se pudo iniciar sesión como '$OWNER' (define LOGIN_USER / LOGIN_PASS)."
PORT="$(kubectl get svc "$UI" -n "$NS" -o jsonpath='{.spec.ports[0].nodePort}')"
PARAMS+=("usuario autenticado: $OWNER" "inundación: 40 clientes anónimos concurrentes durante ${SECS}s sobre GET /api/drug" "sondas (misma IP): 1 par anónimo/autenticado por segundo")

# Script que corre DENTRO del nodo
NODE_SCRIPT="$WORK/r4-node.sh"
cat > "$NODE_SCRIPT" <<'EOS'
#!/bin/bash
N="$1"; T="$2"; SECS="$3"
( seq 1 400000 | timeout "$SECS" xargs -P 40 -I{} curl -s -o /dev/null --max-time 5 "$N/api/drug" ) >/dev/null 2>&1 &
sleep 5                                    # la inundación agota primero el cubo anónimo de la IP
END=$(( $(date +%s) + SECS - 5 ))
while [ "$(date +%s)" -lt "$END" ]; do
  a=$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "$N/api/drug")
  b=$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 -H "Authorization: $T" "$N/api/drug")
  echo "PAIR anon=$a auth=$b"
  sleep 0.5
done
RETRY=$(curl -si --max-time 5 "$N/api/drug" | tr -d '\r' | grep -i '^retry-after' | cut -d' ' -f2)
echo "RETRYAFTER=${RETRY:-none}"
wait
# recuperación: segundos hasta que un anónimo vuelve a recibir 200
S=0
while [ "$S" -lt 120 ]; do
  [ "$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "$N/api/drug")" = 200 ] && break
  S=$((S + 1)); sleep 1
done
echo "RECOVERY=$S"
EOS

probe_start
phase antes; hold "$PRE_SECS"

phase durante
# El script viaja codificado dentro del comando de ssh: no depende de rutas de archivo ni de `minikube cp`
# (que en Windows interpreta "C:/..." como nodo:ruta).
minikube ssh "echo $(base64 -w0 "$NODE_SCRIPT") | base64 -d > /tmp/r4-node.sh" >/dev/null 2>&1 || die "No se pudo copiar el script al nodo."
on_cleanup "minikube ssh 'rm -f /tmp/r4-node.sh' >/dev/null 2>&1"
RESULT="$(minikube ssh "bash /tmp/r4-node.sh http://localhost:$PORT $TOKEN $SECS" 2>/dev/null | tr -d '\r')"

phase despues; hold "$POST_SECS"
probe_stop

PAIRS="$(echo "$RESULT" | grep -c '^PAIR')"
AUTH_OK="$(echo "$RESULT" | grep -c '^PAIR.* auth=200')"
ANON_429="$(echo "$RESULT" | grep -c '^PAIR anon=429')"
ANON_OK="$(echo "$RESULT" | grep -c '^PAIR anon=200')"
RETRY="$(echo "$RESULT" | sed -n 's/^RETRYAFTER=//p')"
RECOVERY="$(echo "$RESULT" | sed -n 's/^RECOVERY=//p')"
PARAMS+=("pares medidos durante la inundación: $PAIRS" "autenticado 200: $AUTH_OK/$PAIRS" "anónimo 429: $ANON_429/$PAIRS (200: $ANON_OK)" "Retry-After del 429: ${RETRY}s" "recuperación del anónimo tras cesar la inundación: ${RECOVERY}s")
log "Pares: $PAIRS | autenticado 200: $AUTH_OK | anónimo 429: $ANON_429 | Retry-After: ${RETRY} | recuperación: ${RECOVERY}s"

assert_true "Se midieron suficientes pares durante la inundación (>= 20)" "$([ "$PAIRS" -ge 20 ] && echo 0 || echo 1)" "$PAIRS pares"
assert_true "El usuario autenticado de la misma IP NO se vio afectado (100% de 200)" "$([ "$PAIRS" -gt 0 ] && [ "$AUTH_OK" -eq "$PAIRS" ] && echo 0 || echo 1)" "$AUTH_OK de $PAIRS"
assert_true "El tráfico anónimo de esa IP SÍ fue frenado (>= 80% de 429)" "$([ "$PAIRS" -gt 0 ] && [ $(( ANON_429 * 100 / PAIRS )) -ge 80 ] && echo 0 || echo 1)" "$ANON_429 de $PAIRS"
assert_true "El 429 informa cuándo reintentar (Retry-After)" "$([[ "$RETRY" =~ ^[0-9]+$ ]] && echo 0 || echo 1)" "Retry-After=${RETRY}"
assert_true "El anónimo se recupera en segundos, no en una hora (<= 30 s)" "$([[ "$RECOVERY" =~ ^[0-9]+$ ]] && [ "$RECOVERY" -le 30 ] && echo 0 || echo 1)" "${RECOVERY}s"
assert_avail durante 99
assert_avail despues 100
restarts_delta "$GATEWAY|$USERS" >/dev/null
assert_eq "Reinicios de gateway/users durante la inundación" "$RESTART_DELTA_TOTAL" 0
note "Si todo el tráfico de una IP es anónimo (p. ej. una oficina entera sin sesión, tras un NAT) el cubo anónimo de esa IP sí se agota para todos: el aislamiento es entre identidades (autenticado vs anónimo) y entre IPs, no entre anónimos de la misma IP."
finish
