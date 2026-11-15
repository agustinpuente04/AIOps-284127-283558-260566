#!/bin/bash
# Librería compartida de la suite de Chaos Engineering de PharmaGo.
# Se carga con:  source "$(dirname "$0")/../lib/common.sh"
# Requiere bash >= 4 (Git Bash en Windows, Linux, macOS con bash de brew), kubectl, curl y awk.

# ---- Entorno ---------------------------------------------------------------------------------
# Git Bash convierte los argumentos que empiezan con "/" en rutas de Windows. Eso rompe
# "kubectl exec ... -- sh -c 'ls /tmp'". Se desactiva la conversión para toda la suite.
export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL='*'

CHAOS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
NS="${NS:-pharmago}"
APPS=(pharmago-ui pharmago-api-gateway pharmago-users-service pharmago-pharmacy-service)
GATEWAY=pharmago-api-gateway
USERS=pharmago-users-service
PHARMACY=pharmago-pharmacy-service
UI=pharmago-ui
DB=pharmago-db
DB_NAME="${DB_NAME:-PharmaDb}"
NETSHOOT_IMAGE="${NETSHOOT_IMAGE:-nicolaka/netshoot:latest}"
GRAFANA_AUTH="${GRAFANA_AUTH:-admin:admin}"
INTERVAL="${INTERVAL:-0.4}"          # segundos entre requests del probe de disponibilidad
ENDPOINTS=("/" "/api/drug" "/api/pharmacy" "/api/users")
DB_PATHS='^/api/(drug|pharmacy)$'     # endpoints que tocan la BD sin autenticación
PHARMACY_PATHS='^/api/(drug|pharmacy)$'
USERS_PATHS='^/api/users$'
REPORTS_DIR="$CHAOS_DIR/reports"

NULLDEV=/dev/null
case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*) NULLDEV=nul ;; esac

SCN_ID=""; SCN_TITLE=""; SECS=300; SECS_OVERRIDDEN=0; PRE_SECS=30; POST_SECS=60
ALERTS=auto; SCN_ARGS=(); URL="${PHARMAGO_URL:-}"
WORK=""; PROBE_CSV=""; PROBE_PID=""
declare -a _CLEANUPS=() RESULTS=() NOTES=() EXPECTED_ALERTS=() FORBIDDEN_ALERTS=() HYPOTHESES=() PARAMS=()
declare -A ALERT_SEEN=() ALERT_BASE=() FORBID_WHY=()
ALERT_T0=0
declare -A _PF_PORT=() _PF_PID=()
PHASE_MARKS=()          # "fase:epoch_ms"
RESTARTS_BEFORE=""
RESTART_DELTA_TOTAL=0; OOM_NEW=0

# ---- Salida ----------------------------------------------------------------------------------
_c() { if [ -t 1 ]; then printf '\033[%sm' "$1"; fi; }
log()  { printf '%s %s[chaos]%s %s\n' "$(date +%T)" "$(_c 36)" "$(_c 0)" "$*"; }
warn() { printf '%s %s[aviso]%s %s\n' "$(date +%T)" "$(_c 33)" "$(_c 0)" "$*" >&2; }
die()  { printf '%s %s[error]%s %s\n' "$(date +%T)" "$(_c 31)" "$(_c 0)" "$*" >&2; exit 2; }
now_ms() { date +%s%3N; }
winpath() { if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else echo "$1"; fi; }

require() {
  local t
  for t in "$@"; do command -v "$t" >/dev/null 2>&1 || die "Falta la herramienta '$t' en el PATH."; done
}

# ---- Cleanup con trap ------------------------------------------------------------------------
# on_cleanup "<comando>" apila un comando de rollback. Se ejecutan en orden inverso al salir,
# incluso con Ctrl+C o si el script falla a mitad de la inyección.
on_cleanup() { _CLEANUPS+=("$1"); }
run_cleanups() {
  local i
  for ((i=${#_CLEANUPS[@]}-1; i>=0; i--)); do
    eval "${_CLEANUPS[i]}" >/dev/null 2>&1 || true
  done
  _CLEANUPS=()
}
_on_exit() {
  local rc=$?
  trap - EXIT INT TERM
  probe_stop 2>/dev/null
  if [ ${#_CLEANUPS[@]} -gt 0 ]; then log "Ejecutando rollback (${#_CLEANUPS[@]} acciones)..."; fi
  run_cleanups
  kill $(jobs -p) 2>/dev/null
  pf_stop_all
  [ -n "$WORK" ] && [ -d "$WORK" ] && rm -rf "$WORK"
  exit "$rc"
}
trap _on_exit EXIT
trap 'exit 130' INT TERM

# ---- Argumentos comunes ----------------------------------------------------------------------
# Antes de llamarla, el escenario fija SECS con su duración por defecto.
parse_common_args() {
  SCN_ARGS=()
  while [ $# -gt 0 ]; do
    case "$1" in
      --secs)       SECS="$2"; SECS_OVERRIDDEN=1; shift 2 ;;
      --url)        URL="$2"; shift 2 ;;
      --pre)        PRE_SECS="$2"; shift 2 ;;
      --post)       POST_SECS="$2"; shift 2 ;;
      --no-alerts)  ALERTS=off; shift ;;
      --alerts)     ALERTS=force; shift ;;
      *)            SCN_ARGS+=("$1"); shift ;;
    esac
  done
}
has_arg() { local a; for a in "${SCN_ARGS[@]}"; do [ "$a" = "$1" ] && return 0; done; return 1; }
arg_val() { # arg_val --flag default
  local i
  for ((i=0; i<${#SCN_ARGS[@]}; i++)); do
    if [ "${SCN_ARGS[i]}" = "$1" ]; then echo "${SCN_ARGS[i+1]:-$2}"; return; fi
  done
  echo "$2"
}

scenario_init() { # scenario_init <ID> <titulo> <duracion_default> "$@"
  SCN_ID="$1"; SCN_TITLE="$2"; SECS="$3"; shift 3
  POST_SECS="${POST_DEFAULT:-$POST_SECS}"     # un escenario puede fijar POST_DEFAULT antes de llamar a scenario_init
  parse_common_args "$@"
  require kubectl curl awk sort
  WORK="$(mktemp -d)"
  PROBE_CSV="$WORK/probe.csv"; : > "$PROBE_CSV"
  mkdir -p "$REPORTS_DIR"
  log "=== $SCN_ID: $SCN_TITLE ==="
  ensure_url
  PARAMS+=("duración de la inyección: ${SECS}s" "URL de la UI: $URL")
}

# ---- URL de la UI ----------------------------------------------------------------------------
# Orden: --url / PHARMAGO_URL -> minikube ip + NodePort -> túnel "minikube service" en background.
ensure_url() {
  if [ -n "$URL" ]; then URL="${URL%/}"; _check_url && return 0; die "No responde $URL"; fi
  local ip port
  ip="$(minikube ip 2>/dev/null)"; port="$(kubectl get svc "$UI" -n "$NS" -o jsonpath='{.spec.ports[0].nodePort}' 2>/dev/null)"
  if [ -n "$ip" ] && [ -n "$port" ]; then
    URL="http://$ip:$port"
    if curl -s -o "$NULLDEV" --max-time 3 "$URL/"; then log "UI en $URL"; return 0; fi
  fi
  log "minikube ip no es accesible desde aquí (driver docker en Windows/macOS). Abriendo túnel 'minikube service'..."
  local tlog="$WORK/tunnel.log" i
  minikube service "$UI" -n "$NS" --url >"$tlog" 2>&1 &
  TUNNEL_PID=$!
  on_cleanup "kill $TUNNEL_PID"
  for i in $(seq 1 40); do
    URL="$(grep -m1 -Eo 'http://[0-9.]+:[0-9]+' "$tlog")"
    [ -n "$URL" ] && break
    sleep 1
  done
  [ -n "$URL" ] || die "No se pudo obtener la URL. Ejecuta 'minikube service $UI -n $NS --url' y pásala con --url o PHARMAGO_URL."
  _check_url || die "El túnel abrió $URL pero no responde."
  log "UI en $URL (túnel minikube)"
}
_check_url() { curl -s -o "$NULLDEV" --max-time 5 "$URL/"; }

# ---- Pods y deployments ----------------------------------------------------------------------
pods_of() {
  # Pods Running sin deletionTimestamp (los Terminating tienen 2 columnas y se descartan)
  kubectl get pods -n "$NS" -l "app=$1" --field-selector=status.phase=Running \
    -o jsonpath='{range .items[*]}{.metadata.name}{" "}{.metadata.deletionTimestamp}{"\n"}{end}' 2>/dev/null \
    | awk 'NF == 1 { print $1 }'
}
pick_pod() { # pick_pod <deploy> [random|first]
  local list; list="$(pods_of "$1")"
  [ -z "$list" ] && return 1
  if [ "${2:-first}" = random ]; then echo "$list" | shuf -n1 2>/dev/null || echo "$list" | head -1; else echo "$list" | head -1; fi
}
svc_ip() { kubectl get svc "$1" -n "$NS" -o jsonpath='{.spec.clusterIP}'; }

deploys_not_ready() {
  kubectl get deploy -n "$NS" -o jsonpath='{range .items[*]}{.metadata.name}{" "}{.spec.replicas}{" "}{.status.readyReplicas}{"\n"}{end}' 2>/dev/null \
    | awk '{ r = ($3 == "" ? 0 : $3); if ($2 != r) print $1 " (" r "/" $2 ")" }'
}
wait_ready() { # wait_ready <timeout_s>
  local end=$(( $(date +%s) + ${1:-180} )) nr
  while [ "$(date +%s)" -lt "$end" ]; do
    nr="$(deploys_not_ready)"
    [ -z "$nr" ] && return 0
    sleep 3
  done
  return 1
}
wait_deploy_ready() { kubectl rollout status "deployment/$1" -n "$NS" --timeout="${2:-180s}" >/dev/null 2>&1; }

steady_state() {
  log "Verificando estado estable..."
  if ! wait_ready 180; then die "El sistema no está sano, no se inyecta caos: $(deploys_not_ready | tr '\n' ' ')"; fi
  local i p code bad=0 n=0
  for i in $(seq 1 20); do
    p="${ENDPOINTS[$(( i % ${#ENDPOINTS[@]} ))]}"
    code="$(curl -s -o "$NULLDEV" -w '%{http_code}' --max-time 5 "$URL$p")"
    n=$((n+1)); if [ "$code" = 000 ] || [ "$code" -ge 500 ]; then bad=$((bad+1)); fi
    sleep 0.3
  done
  [ "$bad" -le 0 ] || die "Estado estable no cumplido: $bad/$n requests fallaron antes de inyectar."
  log "Estado estable OK (todos los deployments listos, $n/$n requests sin error)."
}

# ---- Probe de disponibilidad -----------------------------------------------------------------
# Corre en background y escribe: epoch_ms,fase,path,codigo,segundos
probe_start() {
  echo "antes" > "$WORK/phase"; : > "$WORK/probe.run"; : > "$PROBE_CSV"
  (
    i=0
    while [ -f "$WORK/probe.run" ]; do
      path="${ENDPOINTS[$(( i % ${#ENDPOINTS[@]} ))]}"
      ph="$(cat "$WORK/phase" 2>/dev/null)"
      out="$(curl -s -o "$NULLDEV" -w '%{http_code} %{time_total}' --max-time 5 "$URL$path")"
      echo "$(date +%s%3N),$ph,$path,${out% *},${out#* }" >> "$PROBE_CSV"
      i=$((i+1)); sleep "$INTERVAL"
    done
  ) &
  PROBE_PID=$!
  PHASE_MARKS+=("antes:$(now_ms)")
}
probe_stop() {
  [ -n "$WORK" ] && rm -f "$WORK/probe.run" 2>/dev/null
  if [ -n "$PROBE_PID" ]; then wait "$PROBE_PID" 2>/dev/null; PROBE_PID=""; fi
}
phase() { # phase <antes|durante|despues>
  echo "$1" > "$WORK/phase"; PHASE_MARKS+=("$1:$(now_ms)")
  log "--- fase: $1 ---"
  # La inyección empieza en la primera fase que no es la línea base (casi siempre "durante"; S4 usa fases con nombre propio)
  if [ "$1" != antes ] && [ "$ALERT_T0" = 0 ]; then ALERT_T0=$(date +%s); alerts_snapshot_base; fi
}
phase_mark_ms() { local m; for m in "${PHASE_MARKS[@]}"; do [ "${m%%:*}" = "$1" ] && echo "${m#*:}"; done | tail -1; }

# probe_stats <fase> [regex_path] -> "n fallos limitadas disponibilidad p50_ms p95_ms"
probe_stats() {
  local phase="$1" re="${2:-.}" t counts p
  t="$(awk -F, -v ph="$phase" -v re="$re" '$2 == ph && $3 ~ re' "$PROBE_CSV")"
  if [ -z "$t" ]; then echo "0 0 0 -1 0 0"; return; fi
  counts="$(echo "$t" | awk -F, '{ n++; if ($4 == "000" || $4 + 0 >= 500) f++; if ($4 == 429) l++ }
                                 END { printf "%d %d %d %.2f", n, f + 0, l + 0, (n - f) * 100 / n }')"
  p="$(echo "$t" | awk -F, '{ printf "%d\n", $5 * 1000 }' | sort -n \
        | awk '{ a[NR] = $1 } END { i = int(NR * 0.95 + 0.999); if (i > NR) i = NR; if (i < 1) i = 1; printf "%d %d", a[int((NR + 1) / 2)], a[i] }')"
  echo "$counts $p"
}
# mttr_since <epoch_ms> <regex_path> -> segundos hasta 5 respuestas OK consecutivas, o "n/a"
mttr_since() {
  awk -F, -v t0="$1" -v re="${2:-.}" '
    $1 >= t0 && $3 ~ re {
      if ($4 != "000" && $4 + 0 < 500) { if (c == 0) first = $1; c++; if (c >= 5) { printf "%.1f", (first - t0) / 1000; found = 1; exit } }
      else c = 0
    }
    END { if (!found) printf "n/a" }' "$PROBE_CSV"
}

# ---- Aserciones ------------------------------------------------------------------------------
_result() { RESULTS+=("$1"$'\x1f'"$2"$'\x1f'"$3"); local tag=PASS col=32; [ "$1" = FAIL ] && { tag=FAIL; col=31; }; [ "$1" = SKIP ] && { tag=SKIP; col=33; }
            printf '   %s[%s]%s %s %s\n' "$(_c $col)" "$tag" "$(_c 0)" "$2" "${3:+($3)}"; }
hypothesis() { HYPOTHESES+=("$1"); log "Hipótesis: $1"; }
note() { NOTES+=("$1"); log "Hallazgo: $1"; }

assert_avail() { # assert_avail <fase> <min%> [regex_path]
  local s; s=($(probe_stats "$1" "${3:-.}"))
  if [ "${s[0]}" = 0 ]; then _result FAIL "Disponibilidad en '$1' >= $2%" "sin muestras"; return; fi
  if awk -v a="${s[3]}" -v m="$2" 'BEGIN { exit !(a >= m) }'; then
    _result PASS "Disponibilidad en '$1'${3:+ [$3]} >= $2%" "${s[3]}% de ${s[0]} requests"
  else
    _result FAIL "Disponibilidad en '$1'${3:+ [$3]} >= $2%" "${s[3]}% de ${s[0]} requests, ${s[1]} fallos"
  fi
}
assert_no_5xx() { # assert_no_5xx <fase> <regex_path>
  local s; s=($(probe_stats "$1" "$2"))
  if [ "${s[0]}" = 0 ]; then _result FAIL "Sin 5xx/timeouts en '$1' [$2]" "sin muestras"; return; fi
  [ "${s[1]}" = 0 ] && _result PASS "Sin 5xx/timeouts en '$1' [$2]" "${s[0]} requests" \
                    || _result FAIL "Sin 5xx/timeouts en '$1' [$2]" "${s[1]} de ${s[0]} fallaron"
}
assert_5xx() { # assert_5xx <fase> <regex_path>  (se espera que HAYA fallos)
  local s; s=($(probe_stats "$1" "$2"))
  if [ "${s[0]}" = 0 ]; then _result FAIL "Hay fallos 5xx/timeouts en '$1' [$2]" "sin muestras"; return; fi
  [ "${s[1]}" -gt 0 ] && _result PASS "Hay fallos 5xx/timeouts en '$1' [$2]" "${s[1]} de ${s[0]} fallaron" \
                      || _result FAIL "Hay fallos 5xx/timeouts en '$1' [$2]" "0 de ${s[0]} fallaron: el corte no tuvo efecto visible"
}
assert_latency_up() { # assert_latency_up <regex_path> <factor> [p50|p95] [fase]
  local m="${3:-p50}" ph="${4:-durante}" b d idx=4; [ "$m" = p95 ] && idx=5
  b=($(probe_stats antes "$1")); d=($(probe_stats "$ph" "$1"))
  if [ "${b[0]}" = 0 ] || [ "${d[0]}" = 0 ]; then _result FAIL "Latencia $m [$1] sube x$2" "sin muestras"; return; fi
  local base="${b[$idx]}"; [ "$base" -lt 10 ] && base=10
  if awk -v d="${d[$idx]}" -v b="$base" -v f="$2" 'BEGIN { exit !(d >= b * f) }'; then
    _result PASS "Latencia $m [$1] sube >= x$2${4:+ en '$4'}" "${b[$idx]}ms -> ${d[$idx]}ms"
  else
    _result FAIL "Latencia $m [$1] sube >= x$2${4:+ en '$4'}" "${b[$idx]}ms -> ${d[$idx]}ms"
  fi
}
assert_latency_flat() { # assert_latency_flat <regex_path> <factor> [p50|p95]  (tolerancia: + 250 ms)
  local m="${3:-p95}" b d idx=5; [ "$m" = p50 ] && idx=4
  b=($(probe_stats antes "$1")); d=($(probe_stats durante "$1"))
  if [ "${b[0]}" = 0 ] || [ "${d[0]}" = 0 ]; then _result FAIL "Latencia $m [$1] sin cambios" "sin muestras"; return; fi
  if awk -v d="${d[$idx]}" -v b="${b[$idx]}" -v f="$2" 'BEGIN { exit !(d <= b * f + 250) }'; then
    _result PASS "Latencia $m [$1] sin cambios relevantes (<= x$2 + 250ms)" "${b[$idx]}ms -> ${d[$idx]}ms"
  else
    _result FAIL "Latencia $m [$1] sin cambios relevantes (<= x$2 + 250ms)" "${b[$idx]}ms -> ${d[$idx]}ms"
  fi
}
assert_p50_below() { # assert_p50_below <fase> <regex_path> <ms>   (los fallos deben ser rápidos, no esperar un timeout)
  local s; s=($(probe_stats "$1" "$2"))
  if [ "${s[0]}" = 0 ]; then _result FAIL "p50 en '$1' [$2] < ${3}ms" "sin muestras"; return; fi
  if [ "${s[4]}" -lt "$3" ]; then _result PASS "p50 en '$1' [$2] < ${3}ms (falla rápido)" "p50=${s[4]}ms, p95=${s[5]}ms"
  else _result FAIL "p50 en '$1' [$2] < ${3}ms (falla rápido)" "p50=${s[4]}ms, p95=${s[5]}ms"; fi
}
# note_latency <regex_path> [p50|p95] [fase]: registra cuánto cambió la latencia como OBSERVACIÓN, sin PASS/FAIL.
# La magnitud de la degradación es un dato del experimento, no un requisito: un sistema que se degrada menos de
# lo previsto no está fallando. Lo que se asserta es la propiedad de resiliencia (disponibilidad, reinicios, aislamiento).
note_latency() {
  local m="${2:-p95}" ph="${3:-durante}" b d idx=5; [ "$m" = p50 ] && idx=4
  b=($(probe_stats antes "$1")); d=($(probe_stats "$ph" "$1"))
  if [ "${b[0]}" = 0 ] || [ "${d[0]}" = 0 ]; then note "Latencia $m [$1]: sin muestras suficientes"; return; fi
  note "Latencia $m [$1] en '$ph': ${b[$idx]}ms -> ${d[$idx]}ms ($(awk -v d="${d[$idx]}" -v b="${b[$idx]}" 'BEGIN { printf "x%.2f", (b > 0 ? d / b : 0) }'))"
}
# pod_cpu_pct <pod>: uso de CPU del contenedor en 3 s, en % de su limits.cpu (cgroup v2). Vacío si no tiene límite.
pod_cpu_pct() {
  _kexec "$1" 'read q p < /sys/fs/cgroup/cpu.max; [ "$q" = max ] && exit 0
a=$(grep usage_usec /sys/fs/cgroup/cpu.stat | cut -d" " -f2); sleep 3; b=$(grep usage_usec /sys/fs/cgroup/cpu.stat | cut -d" " -f2)
echo $(( (b - a) * p / (q * 30000) ))' 2>/dev/null | tr -d '\r' | grep -E '^[0-9]+$'
}
# assert_cpu_saturated <pod> [min%=90]: valida el EXPERIMENTO (que el estímulo llegó), no la hipótesis.
# Si la CPU no se saturó, el resto de las conclusiones del escenario no valen.
assert_cpu_saturated() {
  local pct; pct="$(pod_cpu_pct "$1")"
  if [ -z "$pct" ]; then _result FAIL "La inyección saturó la CPU de $1 (>= ${2:-90}% de su límite)" "no se pudo medir (¿sin limits.cpu?)"; return; fi
  [ "$pct" -ge "${2:-90}" ] && _result PASS "La inyección saturó la CPU de $1 (>= ${2:-90}% de su límite)" "${pct}%" \
                            || _result FAIL "La inyección saturó la CPU de $1 (>= ${2:-90}% de su límite)" "${pct}%: el estímulo no llegó"
}
assert_eq() { [ "$2" = "$3" ] && _result PASS "$1" "= $3" || _result FAIL "$1" "obtenido '$2', esperado '$3'"; }
assert_true() { # assert_true <desc> <rc 0/1> [detalle]
  [ "$2" = 0 ] && _result PASS "$1" "$3" || _result FAIL "$1" "$3"
}
skip() { _result SKIP "$1" "$2"; }

# ---- Port-forward genérico (Grafana, Prometheus, gateway) ------------------------------------
free_port() {
  local p i
  for i in $(seq 1 30); do
    p=$(( 20000 + RANDOM % 20000 ))
    (exec 3<>"/dev/tcp/127.0.0.1/$p") 2>/dev/null || { echo "$p"; return 0; }
  done
  return 1
}
pf_start() { # pf_start <clave> <svc> <puerto_remoto>  -> deja el puerto local en _PF_PORT[clave]
  [ -n "${_PF_PORT[$1]}" ] && return 0
  local p; p="$(free_port)" || return 1
  kubectl port-forward -n "$NS" "svc/$2" "$p:$3" >"$WORK/pf-$1.log" 2>&1 &
  _PF_PID[$1]=$!; _PF_PORT[$1]=$p
  local i; for i in $(seq 1 20); do
    grep -q "Forwarding from" "$WORK/pf-$1.log" 2>/dev/null && return 0
    sleep 0.5
  done
  kill "${_PF_PID[$1]}" 2>/dev/null; unset "_PF_PORT[$1]" "_PF_PID[$1]"; return 1
}
pf_stop() { [ -n "${_PF_PID[$1]}" ] && kill "${_PF_PID[$1]}" 2>/dev/null; unset "_PF_PORT[$1]" "_PF_PID[$1]"; }
pf_stop_all() { local k; for k in "${!_PF_PID[@]}"; do kill "${_PF_PID[$k]}" 2>/dev/null; done; }

# ---- Alertas de Grafana ----------------------------------------------------------------------
alerts_active() {
  [ "$ALERTS" = off ] && return 1
  [ "$ALERTS" = force ] && return 0
  [ "$SECS_OVERRIDDEN" = 1 ] && return 1   # con duración recortada las alertas (rate[5m] + for) no llegan a dispararse
  return 0
}
expect_alert() { EXPECTED_ALERTS+=("$1"); }   # fragmento del título de la alerta
_firing_names() {
  pf_start grafana grafana 3000 || return 1
  curl -s --max-time 5 -u "$GRAFANA_AUTH" "http://127.0.0.1:${_PF_PORT[grafana]}/api/alertmanager/grafana/api/v2/alerts?active=true&silenced=false&inhibited=false" \
    | grep -o '"alertname":"[^"]*"' | cut -d'"' -f4
}
alerts_snapshot_base() {
  [ ${#EXPECTED_ALERTS[@]} -eq 0 ] && return 0; alerts_active || return 0
  local names f; names="$(_firing_names)"
  for f in "${EXPECTED_ALERTS[@]}"; do
    if echo "$names" | grep -qF -- "$f"; then ALERT_BASE[$f]=1; fi
  done
}
alerts_poll() {
  [ "$ALERT_T0" -gt 0 ] || return 0          # solo desde que empieza la inyección
  local names f; names="$(_firing_names)" || return 0
  for f in "${EXPECTED_ALERTS[@]}" "${FORBIDDEN_ALERTS[@]}"; do
    [ -n "${ALERT_SEEN[$f]}" ] && continue
    if echo "$names" | grep -qF -- "$f"; then
      ALERT_SEEN[$f]=$(( $(date +%s) - ALERT_T0 )); log "Alerta '$f' disparada tras ${ALERT_SEEN[$f]}s"
    fi
  done
}
# forbid_alert <fragmento> <motivo>: la alerta NO debería dispararse; sirve para documentar huecos de cobertura.
# Se vigila durante toda la inyección (hold) y se evalúa en verdict.
forbid_alert() { FORBIDDEN_ALERTS+=("$1"); FORBID_WHY[$1]="$2"; }
alerts_verdict() {
  local f
  for f in "${FORBIDDEN_ALERTS[@]}"; do
    if ! alerts_active; then skip "Alerta '$f' NO se dispara" "omitida (--no-alerts o --secs recortado)"
    elif ! _firing_names >/dev/null 2>&1; then skip "Alerta '$f' NO se dispara" "Grafana no accesible"
    elif [ -n "${ALERT_SEEN[$f]}" ]; then _result FAIL "Alerta '$f' NO se dispara (${FORBID_WHY[$f]})" "se disparó a los ${ALERT_SEEN[$f]}s: el hueco ya no existe"
    else _result PASS "Alerta '$f' NO se dispara (${FORBID_WHY[$f]})" "confirma el hueco de cobertura"; fi
  done
  for f in "${EXPECTED_ALERTS[@]}"; do
    if ! alerts_active; then skip "Alerta '$f' disparada" "omitida (--no-alerts o --secs recortado: usa la duración por defecto)"
    elif [ -n "${ALERT_BASE[$f]}" ]; then skip "Alerta '$f' disparada" "ya estaba activa antes de inyectar; no es atribuible"
    elif [ -n "${ALERT_SEEN[$f]}" ]; then _result PASS "Alerta '$f' disparada" "MTTD ${ALERT_SEEN[$f]}s"
    elif ! _firing_names >/dev/null 2>&1; then skip "Alerta '$f' disparada" "Grafana no accesible"
    else _result FAIL "Alerta '$f' disparada" "no se disparó en ${SECS}s"; fi
  done
}
# hold <segundos>: espera mientras sondea las alertas esperadas.
hold() {
  local secs="$1" end=$(( $(date +%s) + $1 )) left last=0
  while [ "$(date +%s)" -lt "$end" ]; do
    if [ $(( ${#EXPECTED_ALERTS[@]} + ${#FORBIDDEN_ALERTS[@]} )) -gt 0 ] && alerts_active; then alerts_poll; fi
    left=$(( end - $(date +%s) )); [ "$left" -le 0 ] && break
    if [ $(( secs - left - last )) -ge 30 ]; then last=$(( secs - left )); log "  ... $((secs - left))/${secs}s"; fi
    sleep $(( left < 10 ? left : 10 ))
  done
}

# ---- Restarts / OOM --------------------------------------------------------------------------
_restarts_now() {
  kubectl get pods -n "$NS" -o jsonpath='{range .items[*]}{.metadata.name}{" "}{.status.containerStatuses[0].restartCount}{" "}{.status.containerStatuses[0].lastState.terminated.reason}{"\n"}{end}' 2>/dev/null
}
restarts_snapshot() { RESTARTS_BEFORE="$(_restarts_now)"; }
# restarts_delta [regex_pod]: llena RESTART_DELTA_TOTAL y OOM_NEW; imprime el detalle
restarts_delta() {
  # Fija variables GLOBALES (RESTART_DELTA_TOTAL, OOM_NEW, RESTART_DELTA_DETAIL). Llamarla como D="$(restarts_delta)" las
  # perdería (el subshell tiene copia propia) y las aserciones leerían un 0 viejo: un PASS falso. Se llama así:
  #   restarts_delta [regex] >/dev/null; usar $RESTART_DELTA_TOTAL / $RESTART_DELTA_DETAIL
  if [ "${BASH_SUBSHELL:-0}" -gt 0 ]; then warn "restarts_delta llamada dentro de un subshell: sus resultados se pierden. Usa 'restarts_delta >/dev/null' y lee RESTART_DELTA_DETAIL."; fi
  local re="${1:-.}" out
  out="$(awk -v re="$re" 'NR == FNR { b[$1] = $2; r[$1] = $3; next }
        $1 ~ re { prev = ($1 in b) ? b[$1] : 0; d = $2 - prev
                  if (d > 0) { print $1 " +" d " (" ($3 == "" ? "?" : $3) ")" } }' <(echo "$RESTARTS_BEFORE") <(_restarts_now))"
  RESTART_DELTA_TOTAL=$(echo "$out" | grep -c . )
  OOM_NEW=$(echo "$out" | grep -c OOMKilled)
  RESTART_DELTA_DETAIL="$out"
  echo "$out"
}

# ---- Escalado con anotación de respaldo ------------------------------------------------------
scale_to() { # scale_to <deploy> <n>  (guarda las réplicas originales para `chaos.sh cleanup`)
  local cur ann
  ann="$(kubectl get deploy "$1" -n "$NS" -o jsonpath='{.metadata.annotations.chaos\.pharmago\.io/original-replicas}' 2>/dev/null)"
  if [ -z "$ann" ]; then
    cur="$(kubectl get deploy "$1" -n "$NS" -o jsonpath='{.spec.replicas}')"
    kubectl annotate deploy "$1" -n "$NS" "chaos.pharmago.io/original-replicas=$cur" --overwrite >/dev/null
  fi
  kubectl scale deploy "$1" -n "$NS" --replicas="$2" >/dev/null
}
scale_restore() { # scale_restore <deploy>
  local ann; ann="$(kubectl get deploy "$1" -n "$NS" -o jsonpath='{.metadata.annotations.chaos\.pharmago\.io/original-replicas}' 2>/dev/null)"
  [ -z "$ann" ] && return 0
  kubectl scale deploy "$1" -n "$NS" --replicas="$ann" >/dev/null
  kubectl annotate deploy "$1" -n "$NS" chaos.pharmago.io/original-replicas- >/dev/null 2>&1
}

# ---- Inyecciones dentro de pods --------------------------------------------------------------
_kexec() { local pod="$1"; shift; kubectl exec -n "$NS" "$pod" -- sh -c "$*"; }
# Mata todo proceso etiquetado con la marca (la marca se arma partida para no autoencontrarse)
_KILL_MARK='for d in /proc/[0-9]*; do p=${d#/proc/}; [ "$p" = "$$" ] && continue; grep -qa "CHAOS_BURN=1" $d/environ 2>/dev/null && kill -9 $p 2>/dev/null; done; true'
kill_burn() { _kexec "$1" "$_KILL_MARK" >/dev/null 2>&1; }

inject_cpu() { # inject_cpu <pod> <segundos> [workers]  -> corre en background, autolimitado por timeout
  local pod="$1" secs="$2" w="${3:-4}" cmd="" i
  for i in $(seq 1 "$w"); do cmd+="CHAOS_BURN=1 timeout $secs sh -c 'while :; do :; done' & "; done
  kubectl exec -n "$NS" "$pod" -- sh -c "${cmd}wait" >/dev/null 2>&1 &
  on_cleanup "kill_burn $pod"
  log "CPU: $w bucles en $pod durante ${secs}s"
}
inject_mem() { # inject_mem <pod> <MB> <segundos>  -> retiene MB reales en RAM (tail guarda el buffer)
  local pod="$1" mb="$2" secs="$3"
  kubectl exec -n "$NS" "$pod" -- sh -c "CHAOS_BURN=1 timeout $secs sh -c '(head -c ${mb}m /dev/zero; sleep $secs) | tail -c ${mb}m >/dev/null'" >/dev/null 2>&1 &
  on_cleanup "kill_burn $pod"
  log "Memoria: reteniendo ${mb}MB en $pod durante ${secs}s"
}
# pod_mem <pod> -> "working_set_bytes limite_bytes"
pod_mem() {
  _kexec "$1" 'cg=/sys/fs/cgroup
if [ -f $cg/memory.current ]; then cur=$(cat $cg/memory.current); lim=$(cat $cg/memory.max); ia=$(grep "^inactive_file " $cg/memory.stat | cut -d" " -f2)
else cur=$(cat $cg/memory/memory.usage_in_bytes); lim=$(cat $cg/memory/memory.limit_in_bytes); ia=$(grep "^total_inactive_file " $cg/memory/memory.stat | cut -d" " -f2); fi
echo $((cur - ia)) $lim' 2>/dev/null
}

# Contenedor efímero con NET_ADMIN. Las reglas viven en el netns del pod, así que sobreviven
# a la salida del contenedor; el que inyecta se queda dormido y las revierte solo al terminar.
_IPT='IPT=iptables; $IPT -S >/dev/null 2>&1 || IPT=iptables-legacy;'
_IFACE='IF=$(ip route | awk "/default/{print \$5; exit}"); [ -n "$IF" ] || IF=eth0;'
net_revert_script() {
  echo "$_IPT $_IFACE tc qdisc del dev \$IF root 2>/dev/null; \$IPT -S OUTPUT 2>/dev/null | grep chaos-pharmago | sed 's/^-A/-D/' | while read r; do \$IPT \$r; done; echo CHAOS-REVERTED"
}
net_inject() { # net_inject <pod> <script_aplicar> <segundos>  -> vuelve cuando la regla está aplicada
  local pod="$1" apply="$2" secs="$3" c="chaos-net-$RANDOM" i st
  local full="$_IPT $_IFACE $apply && echo CHAOS-OK || echo CHAOS-FAIL; sleep $(( secs + 20 )); $(net_revert_script)"
  kubectl debug -n "$NS" "$pod" --image="$NETSHOOT_IMAGE" --profile=netadmin --container="$c" -i --quiet -- sh -c "$full" \
      >"$WORK/net-$c.log" 2>&1 </dev/null &
  on_cleanup "net_revert $pod"
  log "Red: aplicando regla en $pod (contenedor efímero $c; la primera vez se descarga $NETSHOOT_IMAGE)..."
  for i in $(seq 1 120); do
    st="$(kubectl get pod "$pod" -n "$NS" -o jsonpath="{.status.ephemeralContainerStatuses[?(@.name==\"$c\")].state.running.startedAt}" 2>/dev/null)"
    [ -n "$st" ] && break
    sleep 2
  done
  [ -n "$st" ] || { cat "$WORK/net-$c.log" >&2; die "El contenedor efímero no arrancó en 240s (¿hay acceso a Docker Hub?)."; }
  sleep 3
  local out; out="$(kubectl logs -n "$NS" "$pod" -c "$c" 2>/dev/null)"
  echo "$out" | grep -q CHAOS-OK || { echo "$out" >&2; die "No se pudo aplicar la regla de red en $pod."; }
}
net_revert() { # revierte tc/iptables de un pod con un contenedor efímero nuevo (idempotente)
  kubectl get pod "$1" -n "$NS" >/dev/null 2>&1 || return 0
  kubectl debug -n "$NS" "$1" --image="$NETSHOOT_IMAGE" --profile=netadmin --container="chaos-rev-$RANDOM" -i --quiet \
      -- sh -c "$_IPT $_IFACE $(net_revert_script)" </dev/null >/dev/null 2>&1
}

# net_preflight <pod>: descarga la imagen en el nodo y comprueba que el perfil netadmin permite tc/iptables.
# Se ejecuta ANTES del baseline para que la descarga (minutos la primera vez) no contamine las mediciones.
net_preflight() {
  log "Preparando herramientas de red ($NETSHOOT_IMAGE); la primera vez puede tardar varios minutos..."
  local out
  out="$(kubectl debug -n "$NS" "$1" --image="$NETSHOOT_IMAGE" --profile=netadmin --container="chaos-pre-$RANDOM" -i --quiet \
        -- sh -c "$_IPT $_IFACE tc qdisc show dev \$IF >/dev/null 2>&1 && \$IPT -S OUTPUT >/dev/null 2>&1 && echo CHAOS-PRE-OK" </dev/null 2>&1)"
  echo "$out" | grep -q CHAOS-PRE-OK || { echo "$out" | tail -5 >&2; return 1; }
}

# ---- SQL Server ------------------------------------------------------------------------------
sql() { # sql <consulta>  -> ejecuta como sa dentro del pod de la BD
  local pw tool
  pw="$(kubectl get secret db-secret -n "$NS" -o jsonpath='{.data.SA_PASSWORD}' | base64 -d)"
  kubectl exec -n "$NS" "deploy/$DB" -c "$DB" -- sh -c \
    "t=/opt/mssql-tools18/bin/sqlcmd; fl=-C; [ -x \$t ] || { t=/opt/mssql-tools/bin/sqlcmd; fl=; }; \$t -S localhost -U sa -P '$pw' \$fl -d ${2:-master} -h -1 -W -b -Q \"$1\"" 2>&1
}

# ---- Generador de carga HTTP (curl --parallel; un único proceso, apto para Git Bash) ---------
# http_batch <método> <path> <cantidad> <concurrencia> <archivo_salida> [body] [spoof_ip=0|1] [base_url]
# Cada línea de salida: "<codigo> <segundos>"
http_batch() {
  local method="$1" path="$2" n="$3" conc="$4" out="$5" body="${6:-}" spoof="${7:-0}" base="${8:-$URL}" cfg i ip
  cfg="$WORK/batch-$RANDOM.cfg"
  : > "$cfg"
  for i in $(seq 1 "$n"); do
    {
      echo "url = \"$base$path\""
      echo "output = \"$NULLDEV\""
      echo 'write-out = "%{http_code} %{time_total}\n"'
      echo 'max-time = 15'
      echo 'silent'
      [ "$method" != GET ] && echo "request = \"$method\""
      if [ -n "$body" ]; then echo "data = \"${body//\"/\\\"}\""; echo 'header = "Content-Type: application/json"'; fi
      if [ "$spoof" = 1 ]; then echo "header = \"X-Real-IP: 10.$((RANDOM % 250)).$((RANDOM % 250)).$((RANDOM % 250 + 1))\""; fi
      [ "$i" -lt "$n" ] && echo 'next'
    } >> "$cfg"
  done
  curl --parallel --parallel-max "$conc" -K "$(winpath "$cfg")" >> "$out" 2>/dev/null
  rm -f "$cfg"
}
# load_summary <archivo> -> "total 2xx 4xx 429 5xx timeouts p95_ms"
load_summary() {
  [ -s "$1" ] || { echo "0 0 0 0 0 0 0"; return; }
  local a p
  a="$(awk '{ n++; c = $1; if (c == "000") t++; else if (c == 429) l++; else if (c >= 500) f++; else if (c >= 400) q++; else if (c >= 200) o++ }
            END { printf "%d %d %d %d %d %d", n, o + 0, q + 0, l + 0, f + 0, t + 0 }' "$1")"
  p="$(awk '{ printf "%d\n", $2 * 1000 }' "$1" | sort -n | awk '{ a[NR] = $1 } END { i = int(NR * 0.95 + 0.999); if (i > NR) i = NR; print a[i] }')"
  echo "$a $p"
}
# load_run <método> <path> <concurrencia> <segundos> <archivo_salida> [body] [spoof] [base_url]
# Mantiene ~<concurrencia> requests en vuelo durante <segundos>.
load_run() {
  local method="$1" path="$2" conc="$3" secs="$4" out="$5" body="${6:-}" spoof="${7:-0}" base="${8:-$URL}"
  local end=$(( $(date +%s) + secs ))
  while [ "$(date +%s)" -lt "$end" ]; do
    http_batch "$method" "$path" $(( conc * 2 )) "$conc" "$out" "$body" "$spoof" "$base"
  done
}

# prom_scalar <promql>: valor numérico de una consulta instantánea en Prometheus (vacío si no hay dato)
prom_scalar() {
  pf_start prom prometheus 9090 || return 1
  curl -s --max-time 10 -G "http://127.0.0.1:${_PF_PORT[prom]}/api/v1/query" --data-urlencode "query=$1"     | sed -n 's/.*"value":\[[^,]*,"\([^"]*\)".*/\1/p' | head -1
}

# ---- Generador de carga DENTRO del nodo de Minikube ------------------------------------------
# Con el driver Docker en Windows/macOS el acceso desde el host pasa por un túnel SSH que se satura con ~100+
# conexiones concurrentes y da timeouts que NO son de la aplicación (verificado: 6000 requests con 200 clientes
# desde dentro del nodo -> 0 timeouts). Para medir la app, la carga se genera desde el nodo.
node_ssh_ok() { minikube ssh "true" >/dev/null 2>&1; }
# owner_token -> token de sesión del usuario de pruebas (LOGIN_USER / LOGIN_PASS). La carga autenticada usa el cubo
# del usuario ("u:{id}") y no el de la IP: así no agota el cubo anónimo que comparte con el probe de disponibilidad.
owner_token() {
  curl -s --max-time 10 -X POST "$URL/api/login" -H 'Content-Type: application/json' \
       -d "{\"userName\":\"${LOGIN_USER:-admin}\",\"password\":\"${LOGIN_PASS:-Abcdef12.}\"}" | sed -n 's/.*"token":"\([^"]*\)".*/\1/p'
}
# load_node <path> <concurrencia> <segundos> <archivo_salida> [spoof=0|1] [ui|gateway] [token]
#   token -> las requests llevan "Authorization: <token>" (usuario autenticado, con su cubo propio)
#   ui      -> NodePort de la UI (nginx -> gateway), igual que un usuario
#   gateway -> ClusterIP del gateway (sin nginx); con spoof=1 rota X-Real-IP y X-Forwarded-For intentando eludir el límite por IP
load_node() {
  local path="$1" conc="$2" secs="$3" out="$4" spoof="${5:-0}" target="${6:-ui}" token="${7:-}" base hdr=""
  if [ "$target" = gateway ]; then base="http://$(svc_ip "$GATEWAY")"
  else base="http://localhost:$(kubectl get svc "$UI" -n "$NS" -o jsonpath='{.spec.ports[0].nodePort}')"; fi
  [ "$spoof" = 1 ] && hdr="-H \"X-Real-IP: {}\" -H \"X-Forwarded-For: {}\""
  [ -n "$token" ] && hdr="$hdr -H \"Authorization: $token\""
  local gen="seq 1 $(( conc * 20 ))"
  [ "$spoof" = 1 ] && gen="awk 'BEGIN{for(i=1;i<=$(( conc * 20 ));i++) printf \"10.%d.%d.%d\\n\", int(i/65025)%250+1, int(i/254)%254+1, i%254+1}'"
  local end=$(( $(date +%s) + secs ))
  while [ "$(date +%s)" -lt "$end" ]; do
    minikube ssh "$gen | xargs -P $conc -I{} curl -s -o /dev/null -w '%{http_code} %{time_total}\n' --max-time 10 $hdr $base$path" 2>/dev/null \
      | tr -d '\r' | grep -E '^[0-9]{3} ' >> "$out"
  done
}

# ---- Informe ---------------------------------------------------------------------------------
verdict() {
  alerts_verdict
  local pass=0 fail=0 skp=0 r
  for r in "${RESULTS[@]}"; do case "${r%%$'\x1f'*}" in PASS) pass=$((pass+1));; FAIL) fail=$((fail+1));; SKIP) skp=$((skp+1));; esac; done
  local ts rep; ts="$(date +%Y%m%d-%H%M%S)"; rep="$REPORTS_DIR/${SCN_ID}-${ts}.md"
  {
    echo "# $SCN_ID - $SCN_TITLE"
    echo; echo "- Fecha: $(date '+%F %T')"; echo "- Resultado: **$([ "$fail" -eq 0 ] && echo PASS || echo FAIL)** ($pass pass, $fail fail, $skp skip)"
    echo; echo "## Hipótesis"; local h; for h in "${HYPOTHESES[@]}"; do echo "- $h"; done
    echo; echo "## Parámetros"; local p; for p in "${PARAMS[@]}"; do echo "- $p"; done
    echo; echo "## Aserciones"; echo "| Resultado | Aserción | Detalle |"; echo "|---|---|---|"
    for r in "${RESULTS[@]}"; do IFS=$'\x1f' read -r a b c <<<"$r"; b="${b//|/\|}"; c="${c//|/\|}"; echo "| $a | $b | $c |"; done
    echo; echo "## Disponibilidad medida (probe sobre la UI)"
    echo "| Fase | Path | Requests | Fallos | 429 | Disp. % | p50 ms | p95 ms |"; echo "|---|---|---|---|---|---|---|---|"
    local ph path s
    for ph in $(awk -F, '!seen[$2]++ { print $2 }' "$PROBE_CSV"); do
      for path in . '^/$' '^/api/drug$' '^/api/pharmacy$' '^/api/users$'; do
        s=($(probe_stats "$ph" "$path")); [ "${s[0]}" = 0 ] && continue
        echo "| $ph | $([ "$path" = . ] && echo todos || echo "${path//[\^\$]/}") | ${s[0]} | ${s[1]} | ${s[2]} | ${s[3]} | ${s[4]} | ${s[5]} |"
      done
    done
    if [ ${#NOTES[@]} -gt 0 ]; then echo; echo "## Hallazgos"; local n; for n in "${NOTES[@]}"; do echo "- $n"; done; fi
  } > "$rep"
  echo
  log "Resultado $SCN_ID: $(_c 1)$pass PASS, $fail FAIL, $skp SKIP$(_c 0)  |  informe: ${rep#"$CHAOS_DIR"/}"
  [ "$fail" -eq 0 ]
}
finish() { verdict; local rc=$?; probe_stop; exit $rc; }
