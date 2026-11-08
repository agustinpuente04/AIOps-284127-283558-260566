#!/bin/bash
# R2 - Inyección de requests: fuerza bruta y password spraying contra POST /api/login.
# Uso: ./r2-fuerza-bruta-login.sh [--secs 360] [--rate 5] [--direct] [--url URL] [--no-alerts]
#   LOGIN_USER / LOGIN_PASS (por defecto admin / Abcdef12., el usuario de la semilla): el "dueño legítimo" que debe
#   seguir entrando mientras dura el ataque. Si no se puede iniciar sesión con él, esas aserciones se omiten.
#
# Fases del ataque:
#   1) dirigido: muchos intentos con clave mala contra UNA cuenta  -> debe bloquearse la cuenta (X-RateLimit-Bucket: account)
#   2) spraying: credenciales aleatorias distintas en cada intento -> lo frena el presupuesto de fallos por IP (login-ip)
source "$(dirname "${BASH_SOURCE[0]}")/../lib/common.sh"
scenario_init R2 "Fuerza bruta y spraying de login (inyección de requests)" 360 "$@"

RATE="$(arg_val --rate 5)"; BASE="$URL"; SPOOF=0
OWNER="${LOGIN_USER:-admin}"; OWNER_PASS="${LOGIN_PASS:-Abcdef12.}"
if has_arg --direct; then
  pf_start gw "$GATEWAY" 80 || die "No se pudo abrir el port-forward al gateway."
  BASE="http://127.0.0.1:${_PF_PORT[gw]}"; SPOOF=1
fi
PARAMS+=("tasa: $RATE logins/s con usuario y clave aleatorios" "modo: $([ "$SPOOF" = 1 ] && echo 'directo con IP rotativa (X-Real-IP / X-Forwarded-For falsificados)' || echo 'vía UI (una sola IP)')" "dueño legítimo: $OWNER")

hypothesis "El login responde solo 400/404/429 (nunca 5xx ni 2xx); una cuenta atacada se bloquea con backoff, el spraying lo frena el presupuesto de fallos por IP y el dueño legítimo sigue entrando durante el ataque."
expect_alert "Logins fallidos anómalos"

# login <usuario> <clave>: imprime "codigo bucket" (bucket = valor de X-RateLimit-Bucket si hubo 429)
login() {
  curl -s -o "$NULLDEV" -D "$(winpath "$WORK/h")" -w '%{http_code}' --max-time 10 -X POST "$BASE/api/login" \
       -H 'Content-Type: application/json' $([ "$SPOOF" = 1 ] && echo "-H X-Forwarded-For:10.$((RANDOM % 250)).$((RANDOM % 250)).7") \
       -d "{\"userName\":\"$1\",\"password\":\"$2\"}"
  printf ' %s\n' "$(grep -i '^x-ratelimit-bucket' "$WORK/h" | tr -d '\r' | cut -d' ' -f2)"
}

steady_state
restarts_snapshot

# El dueño legítimo entra varias veces (sin gastar presupuesto: los éxitos son gratis) para quedar como dispositivo
# conocido en las dos réplicas del gateway.
OWNER_OK=1
for i in 1 2 3 4 5 6 7 8; do
  read -r code _ <<<"$(login "$OWNER" "$OWNER_PASS")"
  [ "$code" = 200 ] || OWNER_OK=0
done
[ "$OWNER_OK" = 1 ] || warn "No se pudo iniciar sesión como '$OWNER': se omiten las aserciones del dueño legítimo (define LOGIN_USER / LOGIN_PASS)."

probe_start
phase antes; hold "$PRE_SECS"

phase durante

# 1) Ataque dirigido a una cuenta que no existe (se bloquea igual que una real: no hay oráculo de cuentas).
#    Con 2 réplicas el estado es por réplica: el bloqueo aparece entre el 5.º y el 10.º intento.
ACC_LOCKS=0; ACC_FIRST=0
for i in $(seq 1 24); do
  read -r code bucket <<<"$(login "objetivo-chaos" "clave-mala-$i")"
  if [ "$code" = 429 ] && [ "$bucket" = account ]; then ACC_LOCKS=$((ACC_LOCKS + 1)); [ "$ACC_FIRST" = 0 ] && ACC_FIRST=$i; fi
done
log "Ataque dirigido: ${ACC_LOCKS} intentos bloqueados por cuenta (primer bloqueo en el intento ${ACC_FIRST:-ninguno})"
PARAMS+=("ataque dirigido (24 intentos a una cuenta): $ACC_LOCKS bloqueados por backoff de cuenta, primer bloqueo en el intento ${ACC_FIRST:-ninguno}")

# 2) Spraying sostenido
OUT="$WORK/login.txt"; : > "$OUT"
end=$(( $(date +%s) + SECS )); last=$(date +%s); lastpoll=0
while [ "$(date +%s)" -lt "$end" ]; do
  t0=$(date +%s)
  http_batch POST /api/login "$RATE" "$RATE" "$OUT" \
    "{\"userName\":\"chaos$RANDOM\",\"password\":\"pw$RANDOM$RANDOM\"}" "$SPOOF" "$BASE"
  if [ $(( $(date +%s) - last )) -ge 30 ]; then last=$(date +%s); log "  ... logins enviados: $(wc -l < "$OUT")"; fi
  # Grafana se consulta cada 15 s como máximo: sondearlo en cada iteración frenaba el ataque a ~1.7/s
  if alerts_active && [ $(( $(date +%s) - lastpoll )) -ge 15 ]; then lastpoll=$(date +%s); alerts_poll; fi
  [ "$(date +%s)" -le "$t0" ] && sleep 1
done
read -r n ok q l f t p95 <<<"$(load_summary "$OUT")"
log "Spraying: $n logins | 2xx=$ok 4xx=$q 429=$l 5xx=$f timeouts=$t | p95=${p95}ms"
PARAMS+=("spraying: $n logins, 2xx=$ok, 4xx=$q, 429=$l, 5xx=$f, timeouts=$t, p95=${p95}ms")

# 3) El dueño legítimo, con el ataque recién terminado y el presupuesto de la IP agotado
OWNER_DURING=0
if [ "$OWNER_OK" = 1 ]; then
  for i in 1 2 3 4 5 6; do read -r code _ <<<"$(login "$OWNER" "$OWNER_PASS")"; [ "$code" = 200 ] && OWNER_DURING=$((OWNER_DURING + 1)); done
fi
PARAMS+=("logins del dueño legítimo durante el ataque: $OWNER_DURING de 6")

phase despues; hold "$POST_SECS"
probe_stop

assert_eq "Login: respuestas 5xx + timeouts" "$(( f + t ))" 0
assert_eq "Login: ningún intento con credenciales aleatorias fue aceptado (2xx)" "$ok" 0
assert_true "Una cuenta atacada se bloquea con backoff (X-RateLimit-Bucket: account)" "$([ "$ACC_LOCKS" -ge 1 ] && echo 0 || echo 1)" "$ACC_LOCKS de 24 intentos bloqueados"
assert_true "El spraying fue frenado por el presupuesto de fallos de la IP (hubo 429)" "$([ "$l" -gt 0 ] && echo 0 || echo 1)" "429=$l de $n"
if [ "$SPOOF" = 1 ]; then
  # falsificar X-Forwarded-For no debe dar más presupuesto: la mayoría de los intentos tiene que haber sido frenada
  assert_true "Rotar X-Forwarded-For no eludió el límite (>= 80% de 429)" "$([ "$n" -gt 0 ] && [ $(( l * 100 / n )) -ge 80 ] && echo 0 || echo 1)" "$([ "$n" -gt 0 ] && echo $(( l * 100 / n )) || echo 0)% con 429"
fi
if [ "$OWNER_OK" = 1 ]; then
  assert_eq "El dueño legítimo siguió entrando durante el ataque (de 6)" "$OWNER_DURING" 6
else
  skip "El dueño legítimo siguió entrando durante el ataque" "no se pudo iniciar sesión con '$OWNER' antes del ataque"
fi
assert_avail durante 99 '^/api/(drug|pharmacy)$'
assert_avail despues 100
note "El estado de los bloqueos vive en memoria de cada réplica del gateway: con 2 réplicas el umbral efectivo de una cuenta puede llegar al doble (aquí: primer bloqueo en el intento ${ACC_FIRST:-?} de 24). Un contador compartido (Redis) lo haría exacto."
restarts_delta "$GATEWAY|$USERS" >/dev/null
assert_eq "Reinicios de gateway/users durante el ataque" "$RESTART_DELTA_TOTAL" 0
finish
