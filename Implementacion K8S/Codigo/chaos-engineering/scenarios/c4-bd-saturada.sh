#!/bin/bash
# C4 - Saturación de la base de datos GENERADA DESDE DENTRO DE SQL SERVER (no con procesos externos como C3).
# Dos etapas seguidas, con fases propias en el informe para poder atribuir cada efecto:
#
#   bloqueo  : un DDL largo (ALTER TABLE ... ADD) sobre la tabla Drugs mantiene un bloqueo de modificación de esquema
#              (Sch-M). La BD tiene READ_COMMITTED_SNAPSHOT = 1, así que un bloqueo exclusivo de fila/tabla NO bloquea a los
#              lectores; el Sch-M sí (es el incidente de "migración sobre una tabla caliente"). Mientras tanto, clientes
#              autenticados siguen pidiendo /api/drug: cada request queda esperando hasta el timeout de comando (30 s).
#              Se mide si el fallo queda acotado a esa tabla o se propaga (agotamiento de hilos / pool de conexiones).
#   pesadas  : N consultas CPU-bound concurrentes (hash SHA-512 por fila, un scheduler cada una) saturan el motor de SQL
#              Server. Se mide la degradación de las rutas que usan la BD.
#
# Rollback: el DDL es transaccional (ROLLBACK), las sesiones se matan con KILL y se comprueba que no queda la columna temporal.
# Uso: ./c4-bd-saturada.sh [--secs 150] [--heavy-secs 120] [--heavy 8] [--clients 60] [--url URL] [--no-alerts]
#   --secs         duración del bloqueo largo
#   --clients      clientes autenticados concurrentes durante el bloqueo (con ~240 se agota el pool de 100 conexiones por réplica)
#   --heavy        consultas pesadas concurrentes
#   LOGIN_USER / LOGIN_PASS (por defecto admin / Abcdef12.): usuario autenticado que genera la carga
source "$(dirname "${BASH_SOURCE[0]}")/../lib/common.sh"
scenario_init C4 "Saturación de la BD (bloqueo largo + consultas pesadas)" 150 "$@"
LOCK_SECS="$SECS"
HEAVY_SECS="$(arg_val --heavy-secs 120)"; HEAVY="$(arg_val --heavy 8)"; CLIENTS="$(arg_val --clients 60)"
OWNER="${LOGIN_USER:-admin}"; OWNER_PASS="${LOGIN_PASS:-Abcdef12.}"
node_ssh_ok || die "C4 necesita 'minikube ssh' (la carga se genera dentro del nodo)."

hypothesis "Un bloqueo largo sobre la tabla Drugs deja sin servicio solo a /api/drug, y falla RÁPIDO (timeout de comando de 5 s + circuit breaker de la entidad): /api/pharmacy y la UI siguen respondiendo, ningún pod se reinicia y todo se recupera solo al liberar el bloqueo."
hypothesis "Con el motor de SQL Server saturado de consultas pesadas la latencia de las rutas con BD sube, pero no hay errores 5xx en la UI ni caída total, y la recuperación es inmediata al terminar."
expect_alert "Tasa de errores 5xx anómala"
expect_alert "Circuit breaker abierto"

HEAVY_Q="SELECT SUM(CONVERT(bigint, CHECKSUM(HASHBYTES('SHA2_512', CONVERT(varchar(20), CONVERT(bigint, a.object_id) + b.object_id))))) FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c OPTION (MAXDOP 1)"

hms() { printf '%02d:%02d:%02d' $(( $1 / 3600 )) $(( $1 % 3600 / 60 )) $(( $1 % 60 )); }
num() { tr -d '\r ' | grep -E '^[0-9]+$' | head -1; }
SA_PASS="$(kubectl get secret db-secret -n "$NS" -o jsonpath='{.data.SA_PASSWORD}' | base64 -d)"
BG_PIDS=()

# db_bg <segundos_máximos> <consulta>: ejecuta sqlcmd dentro del pod de la BD en segundo plano; `timeout` lo corta solo.
db_bg() {
  kubectl exec -n "$NS" "deploy/$DB" -c "$DB" -- sh -c \
    "t=/opt/mssql-tools18/bin/sqlcmd; fl=-C; [ -x \$t ] || { t=/opt/mssql-tools/bin/sqlcmd; fl=; }; timeout $1 \$t -S localhost -U sa -P '$SA_PASS' \$fl -d $DB_NAME -b -Q \"$2\"" >/dev/null 2>&1 &
  BG_PIDS+=($!)
}
# Mata las sesiones de sqlcmd (revierte el DDL abierto) y cualquier resto de la columna temporal.
db_release() {
  [ ${#BG_PIDS[@]} -gt 0 ] && kill "${BG_PIDS[@]}" 2>/dev/null
  BG_PIDS=()
  sql "DECLARE @k nvarchar(max)=''; SELECT @k+='KILL '+CAST(session_id AS varchar(10))+';' FROM sys.dm_exec_sessions WHERE program_name='SQLCMD' AND session_id<>@@SPID AND is_user_process=1; EXEC(@k);" master >/dev/null 2>&1
  sql "IF COL_LENGTH('dbo.Drugs','chaos_lock') IS NOT NULL ALTER TABLE dbo.Drugs DROP COLUMN chaos_lock" "$DB_NAME" >/dev/null 2>&1
}
# Bloqueo de esquema (Sch-M) concedido sobre alguna tabla de PharmaDb: prueba directa de que el DDL abierto está reteniendo el bloqueo
db_lock_held() { sql "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.dm_tran_locks WHERE request_mode='Sch-M' AND request_status='GRANT' AND resource_type='OBJECT' AND resource_database_id=DB_ID('$DB_NAME')" master | num; }
db_blocked()  { sql "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id <> 0" master | num; }
db_appconns() { sql "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE program_name LIKE '%SqlClient%'" master | num; }
db_running()  { sql "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.dm_exec_requests r JOIN sys.dm_exec_sessions s ON s.session_id=r.session_id WHERE s.program_name='SQLCMD' AND r.session_id<>@@SPID" master | num; }
db_cpu_pct()  { kubectl exec -n "$NS" "deploy/$DB" -c "$DB" -- sh -c 'a=$(grep usage_usec /sys/fs/cgroup/cpu.stat | cut -d" " -f2); sleep 3; b=$(grep usage_usec /sys/fs/cgroup/cpu.stat | cut -d" " -f2); echo $(( (b - a) / 30000 ))' 2>/dev/null | num; }

steady_state
restarts_snapshot
sql "SELECT 1" master >/dev/null 2>&1 || die "No se pudo ejecutar sqlcmd en $DB."
TOKEN="$(curl -s --max-time 10 -X POST "$URL/api/login" -H 'Content-Type: application/json' \
         -d "{\"userName\":\"$OWNER\",\"password\":\"$OWNER_PASS\"}" | sed -n 's/.*"token":"\([^"]*\)".*/\1/p')"
[ -n "$TOKEN" ] || die "No se pudo iniciar sesión como '$OWNER' (define LOGIN_USER / LOGIN_PASS)."
on_cleanup db_release
PARAMS+=("tabla bloqueada: dbo.Drugs (Sch-M, ${LOCK_SECS}s)" "clientes autenticados durante el bloqueo: $CLIENTS" "consultas pesadas concurrentes: $HEAVY durante ${HEAVY_SECS}s (MAXDOP 1 cada una)")

probe_start
phase antes; hold "$PRE_SECS"

# ---------------- Etapa 1: bloqueo largo ----------------
phase bloqueo
db_bg $(( LOCK_SECS + 15 )) "SET XACT_ABORT ON; BEGIN TRAN; ALTER TABLE dbo.Drugs ADD chaos_lock int NULL; WAITFOR DELAY '$(hms $(( LOCK_SECS + 10 )))'; ROLLBACK;"
sleep 3
log "Bloqueo Sch-M activo sobre dbo.Drugs; $CLIENTS clientes autenticados piden /api/drug"
LOCK_LOAD="$WORK/lock-load.txt"; : > "$LOCK_LOAD"
load_node /api/drug "$CLIENTS" "$LOCK_SECS" "$LOCK_LOAD" 0 ui "$TOKEN" &
LOAD_PID=$!
hold $(( LOCK_SECS / 2 ))
LOCK_HELD="$(db_lock_held)"; BLOCKED_PEAK="$(db_blocked)"; APP_CONNS="$(db_appconns)"
log "A mitad del bloqueo: Sch-M retenido=${LOCK_HELD:-?}, ${BLOCKED_PEAK:-?} requests bloqueadas en SQL, ${APP_CONNS:-?} conexiones de la app a la BD"
hold $(( LOCK_SECS - LOCK_SECS / 2 ))
wait "$LOAD_PID" 2>/dev/null
INFLIGHT_PEAK="$(prom_scalar "max_over_time(max(pharmago_http_requests_in_flight{job=\"otlp-collector\"})[$(( LOCK_SECS + 20 ))s:5s])")"
read -r ln lok lq l429 l5 lto lp95 <<<"$(load_summary "$LOCK_LOAD")"
PARAMS+=("durante el bloqueo: ${BLOCKED_PEAK:-?} requests bloqueadas en SQL, ${APP_CONNS:-?} conexiones de la app, pico de requests en curso: ${INFLIGHT_PEAK%.*}")
PARAMS+=("carga autenticada durante el bloqueo: $ln requests, 2xx=$lok, 429=$l429, 5xx=$l5, timeouts=$lto")

# ---------------- Liberación y recuperación ----------------
log "Liberando el bloqueo (ROLLBACK del DDL)"
phase recuperacion
db_release
hold 45

# ---------------- Etapa 2: consultas pesadas ----------------
phase pesadas
for i in $(seq 1 "$HEAVY"); do db_bg "$HEAVY_SECS" "$HEAVY_Q"; done
sleep 8
RUNNING="$(db_running)"; log "Consultas pesadas en ejecución dentro de SQL: ${RUNNING:-?} de $HEAVY"
hold $(( HEAVY_SECS / 2 ))
CPU_PCT="$(db_cpu_pct)"
log "CPU del pod de la BD: ${CPU_PCT:-?}% de su límite"
hold $(( HEAVY_SECS / 2 - 8 ))
db_release
PARAMS+=("durante las consultas pesadas: ${RUNNING:-?} de $HEAVY en ejecución, CPU de la BD al ${CPU_PCT:-?}% de su límite")

phase despues; hold "$POST_SECS"
probe_stop

MTTR="$(mttr_since "$(phase_mark_ms recuperacion)" '^/api/drug$')"
PARAMS+=("MTTR de /api/drug tras liberar el bloqueo: ${MTTR}s")
LEFT_COL="$(sql "SET NOCOUNT ON; SELECT ISNULL(COL_LENGTH('dbo.Drugs','chaos_lock'), 0)" "$DB_NAME" | num)"

# Etapa 1
# Con el circuit breaker, las requests ya no se acumulan dentro de SQL Server (BLOCKED_PEAK baja de ~150 a ~0): eso es la mejora, no un fallo.
# Lo que debe verificarse es que el bloqueo ESTABA puesto.
assert_true "El bloqueo de esquema (Sch-M) estaba retenido en SQL Server a mitad de la etapa" "$([ "${LOCK_HELD:-0}" -ge 1 ] && echo 0 || echo 1)" "${LOCK_HELD:-0} bloqueo(s) Sch-M; ${BLOCKED_PEAK:-0} requests esperando dentro de SQL"
assert_5xx bloqueo '^/api/drug$'
assert_no_5xx bloqueo '^/$'
assert_no_5xx bloqueo '^/api/pharmacy$'
assert_p50_below bloqueo '^/api/drug$' 1500
assert_true "Recuperación automática al liberar el bloqueo (MTTR <= 90 s)" "$([ "$MTTR" != n/a ] && awk -v m="$MTTR" 'BEGIN{exit !(m<=90)}' && echo 0 || echo 1)" "MTTR=${MTTR}s"
# Etapa 2
assert_true "Las consultas pesadas saturaron la CPU de la BD (>= 80% de su límite)" "$([ "${CPU_PCT:-0}" -ge 80 ] && echo 0 || echo 1)" "${CPU_PCT:-0}%"
assert_latency_up "$DB_PATHS" 1.5 p95 pesadas
assert_no_5xx pesadas '^/$'
assert_avail pesadas 95
# Global
assert_avail despues 100
assert_eq "La columna temporal del bloqueo no quedó en la tabla (rollback completo)" "${LEFT_COL:-?}" 0
restarts_delta >/dev/null; D="$RESTART_DELTA_DETAIL"
[ "$RESTART_DELTA_TOTAL" -gt 0 ] && note "La saturación de la BD provocó reinicios de pods (probablemente liveness probes por falta de hilos): $(echo "$D" | tr '\n' ';')"
assert_eq "Pods reiniciados durante la saturación de la BD" "$RESTART_DELTA_TOTAL" 0
assert_true "Todos los deployments Ready al terminar" "$(wait_ready 180 && echo 0 || echo 1)"
note "Sin resiliencia, una request a una tabla bloqueada espera el timeout de comando de SqlClient (30 s) ocupando un hilo y una conexión del pool (100 por réplica): con decenas de requests bloqueadas el servicio se queda sin hilos, falla /health y Kubernetes lo reinicia. Con CommandTimeout de 5 s y un circuit breaker por entidad (Backend/RESILIENCIA_BD.md) el fallo queda acotado a la tabla afectada y las requests siguientes fallan en milisegundos con 503."
finish
