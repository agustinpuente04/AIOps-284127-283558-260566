#!/bin/bash
# D1 - Desconexión de componentes: elimina un pod aleatorio de las apps (UI, gateway, users, pharmacy) cada 30 s.
# Valida lo documentado en README-k8s.md: 2 réplicas + PDB + readiness + preStop => 100% de disponibilidad.
# Uso: ./d1-kill-pods-aleatorio.sh [--secs 300] [--abrupt] [--every 30] [--url URL]
#   --abrupt  borra con --grace-period=0 --force (equivale a perder el pod sin preStop ni SIGTERM)
source "$(dirname "${BASH_SOURCE[0]}")/../lib/common.sh"
scenario_init D1 "Eliminación aleatoria de pods de la aplicación" 300 "$@"
EVERY="$(arg_val --every 30)"
if has_arg --abrupt; then
  FORCE=(--grace-period=0 --force); MIN_AVAIL=95; KIND="abrupta (--grace-period=0 --force)"
  hypothesis "Aun perdiendo pods de golpe, la otra réplica sostiene el servicio: disponibilidad >= 95%."
else
  FORCE=(); MIN_AVAIL=100; KIND="ordenada (SIGTERM + preStop)"
  hypothesis "Eliminar un pod a la vez no interrumpe el servicio: 2 réplicas + PDB + readiness + preStop => disponibilidad del 100%."
fi

steady_state
restarts_snapshot
PARAMS+=("un pod aleatorio de {${APPS[*]}} cada ${EVERY}s, eliminación $KIND")
probe_start
phase antes; hold "$PRE_SECS"

phase durante
KILLS=0; T0=$(date +%s)
while [ $(( $(date +%s) - T0 )) -lt "$SECS" ]; do
  APP="${APPS[$(( RANDOM % ${#APPS[@]} ))]}"
  POD="$(pick_pod "$APP" random)" || { warn "sin pods de $APP"; sleep 5; continue; }
  log "Eliminando $POD"
  kubectl delete pod "$POD" -n "$NS" "${FORCE[@]}" --wait=false >/dev/null 2>&1
  KILLS=$((KILLS + 1))
  # Primero esperar a que el pod desaparezca (si no, rollout status devuelve al instante) y luego a que
  # el deployment tenga todas sus réplicas Ready: nunca se elimina otra réplica mientras esta se repone.
  for _ in $(seq 1 90); do kubectl get pod "$POD" -n "$NS" >/dev/null 2>&1 || break; sleep 2; done
  wait_deploy_ready "$APP" 180s || warn "$APP tardó en recuperar sus réplicas"
  left=$(( EVERY - ($(date +%s) - T0) % EVERY )); [ "$left" -gt 0 ] && [ "$left" -le "$EVERY" ] && sleep "$left"
done
PARAMS+=("pods eliminados: $KILLS")

wait_ready 180
phase despues; hold "$POST_SECS"
probe_stop

assert_avail durante "$MIN_AVAIL"
assert_avail despues 100
assert_true "Todos los deployments Ready al terminar" "$(wait_ready 120 && echo 0 || echo 1)"
finish
