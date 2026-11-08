#!/bin/bash
# R3 - Inyección de requests: payloads malformados, gigantes y maliciosos.
# Cada caso se envía por separado (< 1 req/s) y debe terminar en 4xx: nunca 5xx ni conexión caída.
# Uso: ./r3-payloads-malformados.sh [--url URL]
source "$(dirname "${BASH_SOURCE[0]}")/../lib/common.sh"
scenario_init R3 "Payloads malformados y maliciosos (inyección de requests)" 0 "$@"
hypothesis "Ante entradas inválidas el sistema responde 4xx (o 429): jamás 5xx ni cortes de conexión, y ningún pod se reinicia."

steady_state
restarts_snapshot
PARAMS+=("casos: uno por payload, ver tabla de aserciones")

BIG2="$WORK/body2mb.json"; BIG35="$WORK/body35mb.json"
{ printf '{"userName":"'; head -c 2000000 /dev/zero | tr '\0' 'A'; printf '","password":"x"}'; } > "$BIG2"
{ printf '{"userName":"'; head -c 35000000 /dev/zero | tr '\0' 'A'; printf '","password":"x"}'; } > "$BIG35"
DEEP="$(printf '[%.0s' $(seq 1 3000))$(printf ']%.0s' $(seq 1 3000))"
LONGURL="/api/drug?$(head -c 12000 /dev/zero | tr '\0' 'a')=1"
JSONH='Content-Type: application/json'

# caso <descripción> <método> <path> [args de curl...]
caso() {
  local desc="$1" method="$2" path="$3"; shift 3
  local code
  code="$(curl -s -o "$NULLDEV" -w '%{http_code}' --max-time 30 -X "$method" "$@" "$URL$path")"
  if [ "$code" = 000 ] || [ "${code:-0}" -ge 500 ]; then _result FAIL "$desc" "HTTP $code (se esperaba 4xx)"; else _result PASS "$desc" "HTTP $code"; fi
  sleep 1.2
}

log "Enviando payloads..."
caso "login: JSON inválido"                          POST /api/login -H "$JSONH" -d '{"userName": "a", '
caso "login: body vacío"                             POST /api/login -H "$JSONH"
caso "login: Content-Type text/plain"                POST /api/login -H 'Content-Type: text/plain' -d 'userName=a&password=b'
caso "login: campos faltantes"                       POST /api/login -H "$JSONH" -d '{}'
caso "login: null"                                   POST /api/login -H "$JSONH" -d 'null'
caso "login: tipos incorrectos"                      POST /api/login -H "$JSONH" -d '{"userName":123,"password":[1,2]}'
caso "login: inyección SQL en userName"              POST /api/login -H "$JSONH" -d "{\"userName\":\"' OR 1=1; DROP TABLE Users;--\",\"password\":\"x\"}"
caso "login: XSS en password"                        POST /api/login -H "$JSONH" -d '{"userName":"a","password":"<script>alert(1)</script>"}'
caso "login: unicode y caracteres de control"        POST /api/login -H "$JSONH" -d '{"userName":"\u0000😀","password":"\u001f"}'
caso "login: JSON anidado 3000 niveles"              POST /api/login -H "$JSONH" -d "$DEEP"
caso "login: body de 2 MB (límite nginx 1 MB)"       POST /api/login -H "$JSONH" --data-binary "@$(winpath "$BIG2")"
caso "login: body de 35 MB (límite Kestrel 30 MB)"   POST /api/login -H "$JSONH" --data-binary "@$(winpath "$BIG35")"
caso "GET con URL de 12 KB"                          GET  "$LONGURL"
caso "GET con path traversal"                        GET  '/api/drug/../../../etc/passwd' --path-as-is
caso "GET con id no numérico"                        GET  '/api/drug/abc'
caso "GET con id desbordado"                         GET  '/api/pharmacy/99999999999999999999'
caso "GET con query de inyección SQL"                GET  '/api/drug?name=%27%20OR%201%3D1--'
caso "GET con cabecera Authorization basura"         GET  /api/users -H 'Authorization: not-a-guid'
caso "Método no permitido (DELETE /api/login)"       DELETE /api/login
caso "Método no permitido (PATCH /api/drug)"         PATCH /api/drug -H "$JSONH" -d '{}'
caso "POST /api/drug sin autenticación"              POST /api/drug -H "$JSONH" -d '{"name":"x"}'

sleep 5
restarts_delta >/dev/null
assert_eq "Pods reiniciados durante los payloads" "$RESTART_DELTA_TOTAL" 0
if wait_ready 60; then rc=0; else rc=1; fi
assert_true "El sistema sigue sano al terminar" "$rc"
finish
