#!/bin/bash
# Orquestador de la suite de Chaos Engineering de PharmaGo.
#
#   ./chaos.sh list                       lista los escenarios
#   ./chaos.sh run <ID> [opciones]        ejecuta un escenario (ej: ./chaos.sh run C1 --secs 120)
#   ./chaos.sh all [--skip R1,S2] [--only C1,M2,D1]   ejecuta varios en secuencia y resume
#   ./chaos.sh cleanup                    rollback de emergencia, idempotente (tras un corte brusco)
#
# Opciones comunes de los escenarios: --secs N  --pre N  --post N  --url URL  --no-alerts  --alerts
# URL de la UI: --url, la variable PHARMAGO_URL, o se obtiene sola (minikube ip / túnel "minikube service").
# Cada corrida deja un informe en reports/<ID>-<fecha>.md. Ver README.md.

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCEN="$DIR/scenarios"

category() {
  case "$1" in
    R*) echo "Inyección de requests";; C*) echo "Sobrecarga de CPU";; M*) echo "Sobrecarga de memoria";;
    S*) echo "Sobrecarga de storage";; N*) echo "Interrupción de red";; D*) echo "Desconexión de componentes";;
    G*) echo "Combinado";; *) echo "?";;
  esac
}
scenario_file() { local id="${1,,}"; ls "$SCEN/$id"-*.sh 2>/dev/null | head -1; }
all_ids() { for f in "$SCEN"/*.sh; do basename "$f" | cut -d- -f1 | tr a-z A-Z; done | sort -k1.1,1.1 -k1.2n; }

cmd_list() {
  printf '%-4s %-28s %-9s %s\n' ID CATEGORÍA DURACIÓN ESCENARIO
  local id f meta
  for id in $(all_ids); do
    f="$(scenario_file "$id")"
    meta="$(grep -m1 '^scenario_init' "$f" | sed -n 's/^scenario_init [A-Z0-9]* "\([^"]*\)" \([0-9]*\).*/\1|\2/p')"
    printf '%-4s %-28s %-9s %s\n' "$id" "$(category "$id")" "$([ "${meta#*|}" = 0 ] && echo '~1 min' || echo "${meta#*|}s")" "${meta%|*}"
  done
}

cmd_run() {
  local id="$1"; shift
  local f; f="$(scenario_file "$id")"
  [ -n "$f" ] || { echo "Escenario '$id' no existe. Usa: $0 list" >&2; exit 2; }
  exec bash "$f" "$@"
}

cmd_all() {
  local skip="" only="" ids=() id
  while [ $# -gt 0 ]; do
    case "$1" in
      --skip) skip=",${2^^},"; shift 2;;
      --only) only=",${2^^},"; shift 2;;
      *) EXTRA+=("$1"); shift;;
    esac
  done
  for id in $(all_ids); do
    [ -n "$only" ] && [[ "$only" != *",$id,"* ]] && continue
    [[ "$skip" == *",$id,"* ]] && continue
    ids+=("$id")
  done
  [ ${#ids[@]} -gt 0 ] || { echo "No hay escenarios para ejecutar." >&2; exit 2; }

  # La URL se resuelve una sola vez para no abrir un túnel por escenario
  source "$DIR/lib/common.sh"
  WORK="$(mktemp -d)"; ensure_url; export PHARMAGO_URL="$URL"

  local -a names rcs; local rc
  for id in "${ids[@]}"; do
    echo; echo "################ $id ($(category "$id")) ################"
    bash "$(scenario_file "$id")" "${EXTRA[@]}"; rc=$?
    names+=("$id"); rcs+=("$rc")
    log "Pausa de 60s y verificación de estado estable antes del siguiente escenario..."
    sleep 60; wait_ready 300 || warn "El sistema no volvió a estado estable: $(deploys_not_ready | tr '\n' ' ')"
  done

  echo; echo "================ RESUMEN ================"
  local i bad=0
  for i in "${!names[@]}"; do
    case "${rcs[i]}" in 0) r="PASS";; 1) r="FAIL"; bad=1;; *) r="ERROR (no se pudo ejecutar)"; bad=1;; esac
    printf '  %-4s %-28s %s\n' "${names[i]}" "$(category "${names[i]}")" "$r"
  done
  exit $bad
}

cmd_cleanup() {
  source "$DIR/lib/common.sh"
  WORK="$(mktemp -d)"
  log "=== Rollback de emergencia ==="
  local d p ann
  # 1. Réplicas originales
  for d in $(kubectl get deploy -n "$NS" -o name 2>/dev/null | cut -d/ -f2); do
    ann="$(kubectl get deploy "$d" -n "$NS" -o jsonpath='{.metadata.annotations.chaos\.pharmago\.io/original-replicas}' 2>/dev/null)"
    [ -n "$ann" ] && { log "Restaurando $d a $ann réplicas"; scale_restore "$d"; }
  done
  # 2. Procesos inyectados y archivos de relleno en los pods de la app y la BD
  for d in "${APPS[@]}" "$DB"; do
    for p in $(pods_of "$d"); do
      kill_burn "$p"; _kexec "$p" 'rm -f /tmp/chaos_fill' >/dev/null 2>&1
    done
  done
  log "Procesos y archivos de relleno eliminados"
  # 3. Reglas de red: solo en pods que alguna vez recibieron un contenedor efímero chaos-net-*
  for d in "$GATEWAY" "$USERS" "$PHARMACY"; do
    for p in $(pods_of "$d"); do
      if kubectl get pod "$p" -n "$NS" -o jsonpath='{.status.ephemeralContainerStatuses[*].name}' 2>/dev/null | grep -q 'chaos-net'; then
        log "Quitando reglas tc/iptables de $p"; net_revert "$p"
      fi
    done
  done
  # 4. BD: tabla de relleno y límites del archivo de datos
  if sql "SELECT 1" master >/dev/null 2>&1; then
    local ln
    ln="$(sql "SET NOCOUNT ON; SELECT TOP 1 name FROM sys.database_files WHERE type=0" "$DB_NAME" | tr -d '\r' | grep -v '^$' | head -1)"
    sql "IF OBJECT_ID('dbo.chaos_fill') IS NOT NULL DROP TABLE dbo.chaos_fill" "$DB_NAME" >/dev/null
    # C4: sesiones sqlcmd abiertas (bloqueo Sch-M y consultas pesadas) y columna temporal del DDL
    sql "DECLARE @k nvarchar(max)=''; SELECT @k+='KILL '+CAST(session_id AS varchar(10))+';' FROM sys.dm_exec_sessions WHERE program_name='SQLCMD' AND session_id<>@@SPID AND is_user_process=1; EXEC(@k);" master >/dev/null 2>&1
    sql "IF COL_LENGTH('dbo.Drugs','chaos_lock') IS NOT NULL ALTER TABLE dbo.Drugs DROP COLUMN chaos_lock" "$DB_NAME" >/dev/null 2>&1
    if [ -n "$ln" ]; then
      sql "ALTER DATABASE [$DB_NAME] MODIFY FILE (NAME = N'$ln', MAXSIZE = UNLIMITED)" master >/dev/null
      sql "ALTER DATABASE [$DB_NAME] MODIFY FILE (NAME = N'$ln', FILEGROWTH = 64MB)" master >/dev/null
    fi
    log "BD restaurada (sin chaos_fill, MAXSIZE ilimitado, FILEGROWTH 64MB = valor por defecto de SQL Server)"
  fi
  # 5. Logs de la inundación
  kubectl exec -n "$NS" deploy/elasticsearch -c elasticsearch -- curl -s -X POST \
    'localhost:9200/pharmago-logs-*/_delete_by_query?q=%22CHAOS-LOG-FLOOD%22&conflicts=proceed' >/dev/null 2>&1 && log "Logs de la inundación borrados"
  wait_ready 180 && log "Todos los deployments Ready." || warn "Aún no están listos: $(deploys_not_ready | tr '\n' ' ')"
}

case "${1:-}" in
  list)    cmd_list ;;
  run)     shift; [ $# -ge 1 ] || { echo "Uso: $0 run <ID> [opciones]" >&2; exit 2; }; cmd_run "$@" ;;
  all)     shift; EXTRA=(); cmd_all "$@" ;;
  cleanup) cmd_cleanup ;;
  *)       sed -n '2,13p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 2 ;;
esac
