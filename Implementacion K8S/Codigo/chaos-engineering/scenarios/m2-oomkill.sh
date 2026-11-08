#!/bin/bash
# M2 - Sobrecarga de memoria SUPERANDO el límite: una réplica de users-service intenta retener más memoria que su limits.memory.
#
# Qué se puede y qué no se puede medir desde dentro del pod (verificado en este cluster):
#  - SÍ: que el kernel hace cumplir el límite (contador oom_kill del cgroup) y que el servicio no se ve afectado.
#  - NO: forzar que muera el CONTENEDOR. El OOM killer elige al proceso con mayor puntaje (el inyectado, 400 MB frente a ~80 MB de
#    dotnet; oom_score_adj de PID 1 puede subirse pero no compensa) y el cgroup es de solo lectura, así que no se puede activar
#    memory.oom.group para que muera todo. Si la víctima fuera el proceso principal, el escenario lo detecta (OOMKilled + reinicio).
# Uso: ./m2-oomkill.sh [--secs 120] [--factor 125] [--url URL] [--no-alerts]
#   --factor  % del límite que se intenta retener (default 125)
source "$(dirname "${BASH_SOURCE[0]}")/../lib/common.sh"
scenario_init M2 "OOMKill de una réplica por exceso de memoria" 120 "$@"
FACTOR="$(arg_val --factor 125)"

hypothesis "Al superar limits.memory el kernel hace cumplir el límite (oom_kill del cgroup) y el servicio no se ve afectado (>= 99%). Si la víctima es el proceso principal, el contenedor se reinicia solo (OOMKilled) y vuelve a Ready."

steady_state
restarts_snapshot
POD="$(pick_pod "$USERS" random)" || die "No hay pods de users-service."
read -r WS LIM <<<"$(pod_mem "$POD")"
[ -n "$LIM" ] && [ "$LIM" -gt 0 ] 2>/dev/null || die "No se pudo leer el cgroup de $POD."
[ "$LIM" -gt 17179869184 ] && die "$POD no tiene límite de memoria; el escenario necesita limits.memory."
MB=$(( LIM * FACTOR / 100 / 1048576 ))
RC0="$(kubectl get pod "$POD" -n "$NS" -o jsonpath='{.status.containerStatuses[0].restartCount}')"
PARAMS+=("pod afectado: $POD" "límite: $((LIM/1048576))Mi" "memoria que se intenta retener: ${MB}Mi (${FACTOR}% del límite)")
oom_kills() { _kexec "$POD" "grep '^oom_kill ' /sys/fs/cgroup/memory.events | cut -d' ' -f2" 2>/dev/null | tr -d '\r '; }
OOM_KILL0="$(oom_kills)"
probe_start
phase antes; hold "$PRE_SECS"

phase durante
inject_mem "$POD" "$MB" "$SECS"
T0=$(date +%s); T_OOM=""; T_READY=""
while [ $(( $(date +%s) - T0 )) -lt "$SECS" ]; do
  RC="$(kubectl get pod "$POD" -n "$NS" -o jsonpath='{.status.containerStatuses[0].restartCount}' 2>/dev/null)"
  READY="$(kubectl get pod "$POD" -n "$NS" -o jsonpath='{.status.containerStatuses[0].ready}' 2>/dev/null)"
  if [ -z "$T_OOM" ] && [ "${RC:-0}" -gt "${RC0:-0}" ]; then T_OOM=$(( $(date +%s) - T0 )); log "Contenedor reiniciado a los ${T_OOM}s"; fi
  if [ -n "$T_OOM" ] && [ "$READY" = true ]; then T_READY=$(( $(date +%s) - T0 )); log "Pod Ready de nuevo a los ${T_READY}s"; break; fi
  sleep 2
done
[ -n "$T_OOM" ] && hold 20

log "Rollback: liberando memoria"
kill_burn "$POD"
phase despues; hold "$POST_SECS"
probe_stop

restarts_delta "$USERS" >/dev/null; D="$RESTART_DELTA_DETAIL"
OOM_KILL1="$(oom_kills)"; OOM_KILL_DELTA=$(( ${OOM_KILL1:-0} - ${OOM_KILL0:-0} ))
PARAMS+=("oom_kill del cgroup antes/después: ${OOM_KILL0:-?} -> ${OOM_KILL1:-?}")
assert_true "El kernel hizo cumplir el límite de memoria (oom_kill del cgroup aumentó)" "$([ "$OOM_KILL_DELTA" -ge 1 ] && echo 0 || echo 1)" "+${OOM_KILL_DELTA}"
if [ "$OOM_NEW" -ge 1 ]; then
  _result PASS "El contenedor fue terminado por OOMKilled" "$D"
  assert_true "El pod volvió a Ready sin intervención" "$([ -n "$T_READY" ] && echo 0 || echo 1)" "OOM a los ${T_OOM}s, Ready a los ${T_READY:-n/a}s"
elif [ "$RESTART_DELTA_TOTAL" -ge 1 ]; then
  _result PASS "El contenedor se reinició" "$D"
else
  skip "El contenedor fue terminado por OOMKilled" "no aplica: el kernel mató solo al proceso inyectado y el contenedor sobrevivió"
  note "El límite de memoria se hizo cumplir sin afectar a la app: el kernel mató al proceso que excedía (el inyectado) y no al proceso principal. No se puede forzar un OOMKill del contenedor desde dentro del pod: el cgroup es de solo lectura (no se puede activar memory.oom.group) y el proceso inyectado puntúa más alto que dotnet. Para ver el reinicio por OOMKilled haría falta que la propia app consumiera la memoria."
fi
assert_avail durante 99
assert_avail despues 100
assert_true "users-service terminó con todas sus réplicas Ready" "$(wait_deploy_ready "$USERS" 120s && echo 0 || echo 1)"
finish
