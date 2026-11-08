#!/bin/bash
# S1 - Sobrecarga de storage: llena el disco del nodo escribiendo en el filesystem efímero de un pod.
#
# SEGURIDAD: el script calcula cuánto habría que escribir para dejar el 14% libre (la alerta salta al 15%)
# y NUNCA deja menos del 11% libre (kubelet desaloja pods por DiskPressure al 10%). Además aplica un
# tope absoluto (--max-mb, default 4096). Si el disco es tan grande que el umbral de alerta es inalcanzable
# con ese tope, llena hasta el tope, verifica que la app no se degrade y marca la alerta como SKIP.
#
# Uso: ./s1-disco-efimero.sh [--secs 420] [--max-mb 4096] [--url URL] [--no-alerts]
source "$(dirname "${BASH_SOURCE[0]}")/../lib/common.sh"
scenario_init S1 "Llenado del disco efímero del nodo" 420 "$@"
MAXMB="$(arg_val --max-mb 4096)"
TARGET_FREE=14; FLOOR_FREE=11

steady_state
restarts_snapshot
POD="$(pick_pod "$GATEWAY" random)" || die "No hay pods del gateway."
read -r SIZE_KB AVAIL_KB <<<"$(_kexec "$POD" "df -Pk /tmp | tail -1 | awk '{print \$2, \$4}'")"
[ -n "$SIZE_KB" ] && [ "$SIZE_KB" -gt 0 ] 2>/dev/null || die "No se pudo leer df de $POD."

SIZE_MB=$(( SIZE_KB / 1024 )); AVAIL_MB=$(( AVAIL_KB / 1024 ))
FREE_PCT=$(( AVAIL_KB * 100 / SIZE_KB ))
NEED_MB=$(( AVAIL_MB - SIZE_MB * TARGET_FREE / 100 ))          # MB a escribir para quedar en TARGET_FREE% libre
SAFE_MB=$(( AVAIL_MB - SIZE_MB * FLOOR_FREE / 100 ))           # máximo permitido para no pasar de FLOOR_FREE% libre
[ "$NEED_MB" -gt 0 ] || die "El nodo ya tiene solo ${FREE_PCT}% libre (< ${TARGET_FREE}%); libera espacio antes de probar."
FILL_MB="$NEED_MB"; REACHABLE=1
if [ "$FILL_MB" -gt "$MAXMB" ]; then FILL_MB="$MAXMB"; REACHABLE=0; fi
[ "$FILL_MB" -gt "$SAFE_MB" ] && FILL_MB="$SAFE_MB"
[ "$FILL_MB" -ge 50 ] || die "Margen insuficiente para llenar disco de forma segura (${FILL_MB}MB)."

PARAMS+=("pod afectado: $POD" "disco del nodo: $((SIZE_MB / 1024))GB, ${FREE_PCT}% libre" "a escribir: ${FILL_MB}MB (necesarios para ${TARGET_FREE}% libre: ${NEED_MB}MB; tope --max-mb: ${MAXMB}MB)")
if [ "$REACHABLE" = 1 ]; then
  hypothesis "Con el disco del nodo al ~$((100 - TARGET_FREE))% la app sigue sirviendo (>= 99%), no se desaloja ningún pod y salta la alerta 'Disco del nodo casi lleno'."
  expect_alert "Disco del nodo casi lleno"
else
  hypothesis "Escribir ${FILL_MB}MB en el disco del nodo no degrada la app (>= 99%) ni desaloja pods."
  note "El disco del nodo es de $((SIZE_MB / 1024))GB: llegar a ${TARGET_FREE}% libre requeriría escribir $((NEED_MB / 1024))GB (tope seguro: ${MAXMB}MB). La alerta 'Disco del nodo casi lleno' no puede probarse de forma segura en este entorno."
  skip "Alerta 'Disco del nodo casi lleno' disparada" "umbral inalcanzable con el tope de ${MAXMB}MB (disco de $((SIZE_MB / 1024))GB)"
fi

probe_start
phase antes; hold "$PRE_SECS"

phase durante
on_cleanup "_kexec $POD 'rm -f /tmp/chaos_fill'"
log "Escribiendo ${FILL_MB}MB en /tmp de $POD..."
_kexec "$POD" "dd if=/dev/zero of=/tmp/chaos_fill bs=1M count=$FILL_MB 2>&1 | tail -1"
read -r _ AVAIL2_KB <<<"$(_kexec "$POD" "df -Pk /tmp | tail -1 | awk '{print \$2, \$4}'")"
FREE2=$(( AVAIL2_KB * 100 / SIZE_KB ))
log "Espacio libre tras el llenado: ${FREE2}%"
PARAMS+=("espacio libre tras llenar: ${FREE2}% (mínimo permitido ${FLOOR_FREE}%)")
hold "$SECS"

log "Rollback: borrando /tmp/chaos_fill"
_kexec "$POD" 'rm -f /tmp/chaos_fill'
phase despues; hold "$POST_SECS"
probe_stop

assert_true "El llenado dejó el espacio libre dentro del margen seguro (>= ${FLOOR_FREE}%)" "$([ "$FREE2" -ge "$FLOOR_FREE" ] && echo 0 || echo 1)" "${FREE2}% libre"
assert_avail durante 99
assert_avail despues 100
restarts_delta >/dev/null; D="$RESTART_DELTA_DETAIL"
assert_eq "Pods reiniciados o desalojados por falta de disco" "$RESTART_DELTA_TOTAL" 0
assert_true "Todos los deployments Ready al terminar" "$(wait_ready 120 && echo 0 || echo 1)"
finish
