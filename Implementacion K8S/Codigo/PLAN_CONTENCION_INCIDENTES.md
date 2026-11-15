# Plan de Contención de Incidentes Operacionales — PharmaGo

## 1. Niveles de severidad

La severidad se define por el impacto real al usuario, no por la etiqueta de la alerta. Ante la duda, elegir el nivel más grave. Escalar de SEV si el incidente empeora, suma otro síntoma o supera 20 minutos sin diagnóstico claro.

| Nivel | Criterio de impacto | Ejemplos en PharmaGo | Respuesta y comunicación |
| :--- | :--- | :--- | :--- |
| **SEV1 (Crítico)** | Servicio caído o datos/cuentas comprometidos | UI no carga, nadie puede loguearse, `pharmago-db` caída, servicio en `0/2` réplicas, disco `<10%` | Trabajan los 3 de inmediato (llamada); actualización c/15 min |
| **SEV2 (Mayor)** | Funcionalidad clave degradada o caída parcial | Solo `/api/drug` devuelve 503, >5% de 5xx, latencia visible por usuarios, disco `<15%` | Mínimo 2 personas; actualización c/30 min |
| **SEV3 (Menor)** | Sin impacto al usuario, cae margen u observabilidad | 1 de 2 réplicas caída, CPU/memoria alta, ataque 429 contenido, Collector o Kibana caídos | 1 persona registra en el hilo y resuelve en el día |

---

## 2. Roles de respuesta (adaptados a 3 personas)

Los roles no son fijos por persona y separan la coordinación de la ejecución técnica:

| Rol Atlassian | Qué hace | Qué evita hacer | Asignación |
| :--- | :--- | :--- | :--- |
| **Incident Commander (`IC`)** | Define SEV, asigna tareas, autoriza acciones de riesgo, declara contenido y marca si está resuelto | No ejecuta comandos ni se pierde investigando | Quien toma primero la alerta (`La tomo, soy IC`) |
| **Technical Lead (`Técnico`)** | Diagnostica (Grafana/Kibana/`kubectl`) y aplica la contención anunciando cada paso | No toma decisiones de riesgo sin OK del `IC` | Quien corre el clúster Minikube (si también era `IC`, cede el mando) |
| **Communications Lead (`Comms`)** | Abre el hilo `INC-AAAAMMDD-n`, lleva la bitácora con hora, mide pulso externo y envía updates | No omite hechos medidos ni deja sin avisar demoras | Tercer integrante |

**Contingencia:** con 2 personas, una es `Técnico` y la otra cubre `IC + Comms`. Con 1 persona: avisar en canal → guardar evidencia → contener. Si un SEV1 no tiene respuesta en 10 min se llama por teléfono, si el daño excede al equipo, el `IC` escala al responsable externo.

---

## 3. Ciclo de vida del incidente y reglas de contención

**1. Detectar** (alerta Grafana/Discord o usuario) → **2. Abrir canal** (hilo `INC-AAAAMMDD-n`) → **3. Evaluar y asignar roles** (pulso + SEV) → **4. Guardar evidencia** → **5. Contener** → **6. Resolver** → **7. Postmortem**.

### Reglas operativas del clúster
1. **Primero contener, después investigar la causa raíz.**
2. **Guardar logs antes de borrar o reiniciar un pod** (los del contenedor previo se pierden al recrearlo).
3. **Un cambio por vez, anunciado antes en el hilo.**
4. **No reiniciar aplicaciones cuando cae `pharmago-db`:** se recuperan solas en 15–45 s al volver la BD y sin ella fallarían sus migraciones de arranque.
5. **Tocar de a una réplica por vez** (cada servicio corre 2 réplicas con `RollingUpdate` y `PodDisruptionBudget`).
6. **Nunca en incidente:** `minikube delete`, `./k8s/cleanup.sh`, tocar `/mnt/data/sql` o ejecutar otros scripts de caos. Todo cambio manual (`kubectl scale/set env`) debe registrarse para pasar al repo o revertirse.

---

## 4. Evluación inicial rápida (primeros 5 minutos)

```bash
# 1. Exponer UI (:4200), Grafana (:3000), Kibana (:5601) y Prometheus (:9090)
(cd k8s && ./port-forward.sh)

# 2. Health check desde el usuario (nota: /api/users sin sesión da 401 sin tocar BD, probar BD con /api/drug)
for p in / /api/drug /api/pharmacy /api/users; do
  printf '%-14s %s\n' "$p" "$(curl -s -o /dev/null -w '%{http_code}  %{time_total}s' --max-time 6 "http://127.0.0.1:4200$p")"
done

# 3. Estado K8s y respaldo mínimo de evidencia previo a reiniciar pods
kubectl -n pharmago get deploy,pods -o wide
kubectl -n pharmago get events --sort-by=.lastTimestamp | tail -20
kubectl -n pharmago logs <pod> --previous --tail=100 > ~/inc-previo.log 2>&1
```

**Consultas clave en Kibana con KQL (`pharmago-logs`):** `LogLevel : "Error"` · `Scopes.correlation_id : "<id>"` · `Scopes.pharma_biz : "login_fail"` · `State.ElapsedMs > 500`.  
**Acceso SQL (`pharmago-db`):** consultar vía `kubectl exec -n pharmago deploy/pharmago-db -c pharmago-db` con `sqlcmd` y contraseña de `secret/db-secret`.

---

## 5. Runbooks de contención (Alertas de Grafana y escenarios del Obligatorio)

| Escenario / Alerta (ventana) | Diagnóstico rápido | Acción de contención inmediata | Qué NO hacer / Listo cuando |
| :--- | :--- | :--- | :--- |
| **Inyección de requests / Fuerza bruta**<br>• *Tráfico anómalo* (`z-score > 3`, ~4m)<br>• *Saturación requests* (`>20`, ≥2m)<br>• *Logins fallidos* (`>5/min`, ~2,5m) | Ver *Overview* (429 y status codes). En fuerza bruta revisar en Kibana `login_fail` y validar si hubo `login_success` en esa cuenta (si entró → **SEV1**). | Si 429 frena sin 5xx/lentitud: observar (cubos por identidad y backoff en login protegen usuarios válidos). Si degrada tráfico legítimo (**OK IC**): ajustar env `RateLimiting__*` en `pharmago-api-gateway` (ej. anónimos 300 o login fallido 3). | **No** reiniciar el gateway salvo necesidad (borra cubos y bloqueos en memoria; 2 réplicas duplican tope).<br>**Listo:** 429 en 0, tasas `<5/min`. |
| **Sobrecarga de CPU / Memoria**<br>• *CPU > 80%* (~7–10m)<br>• *Memoria > 85% / OOMKill* (≥3m) | Ver *Infra* y `describe pod` (`Last State`). Distinguir si escala 1 pod o todas las réplicas del servicio. | **1 pod:** guardar logs y `kubectl -n pharmago delete pod <pod>` (la otra réplica sostiene).<br>**Todas:** CPU → escalar a 3 réplicas; Memoria (**OK IC**) → `set resources --limits=memory=512Mi` o rollback si vino de deploy. | **No** borrar dos réplicas a la vez; validar memoria libre del nodo antes de subir límites.<br>**Listo:** CPU `<60%`, Mem `<70%` por 15m. |
| **Sobrecarga de Storage / Disco**<br>• *Disco del nodo < 15%* (≥5m; `<10%` SEV1 eviction) | `minikube ssh -- df -h`, `du -sh /mnt/data/*` y `docker system df`. En BD revisar `EXEC sp_helpfile` (tope Express 10 GB). | 1) `(cd chaos-engineering && ./chaos.sh cleanup)`<br>2) `minikube ssh -- docker image prune -f`<br>3) (**OK IC**) borrar índices antiguos `pharmago-logs-*` en Elasticsearch vía HTTP `DELETE`. | **Nunca** borrar `/mnt/data/sql` ni datos de Prometheus.<br>**Listo:** `>20%` libre en nodo. |
| **Interrupción de Red / Latencia**<br>• *Latencia anómala* (`>3x` 1h y `>200ms`, ≥3m) o lentitud sin alerta | Alertas miden dentro del pod (ciegas a latencia de red). Medir desde afuera con `curl` y comparar con *Overview* / BD / GC. | Si es externo sin alza en Grafana: verificar reglas de red residuales (`./chaos.sh cleanup`) o reciclar pods (`kubectl -n pharmago rollout restart deploy/<svc>`). Revisar límite de túnel Windows. | **No** asumir que está sano solo porque Grafana está verde.<br>**Listo:** latencia externa en decenas de ms. |
| **Desconexión de componentes / BD**<br>• *Servicio sin réplicas* (~2,5m)<br>• *Circuit breaker abierto* (45–95s)<br>• *Errores 5xx > 5%* (~4–5m) | Pulso `/api/drug` + `get deploy` + `describe pod`. Ver *Negocio y Runtime* (cuántos `target` abiertos) y `sqlcmd SELECT 1`. | **Servicio `0/2`:** `scale --replicas=2`; si `Pending`, `node-type=all`; si `ErrImageNeverPull`, `./k8s/build-images.sh`.<br>**Breaker en 1 tabla:** buscar sesión `Sch-M` / top CPU en SQL y `KILL <id>` (**OK IC**).<br>**BD caída:** diagnosticar pod `pharmago-db` o `rollout restart deploy/pharmago-db` (**OK IC**). | **No** reiniciar apps si cayó la BD (`/health` no valida BD, los pods siguen `Ready`).<br>**Listo:** deploys `2/2`, breakers en 0, `/api/drug` en 200. |
| **Deploy fallido / Telemetría caída**<br>• Error tras release o dashboards planos / sin alertas | `get pods` (`AGE`) y `logs <pod>`. Si no hay datos de app (Collector caído silencia 6 alertas sin avisar), chequear `otlp-collector`. | **Deploy:** `rollout undo` solo frena rollout trabado; para código anterior (`:latest`), reconstruir desde commit bueno y cargar con `docker save ... \| minikube image load -`.<br>**OTel:** `rollout restart deploy/otlp-collector`; si falta Discord, recrear secreto `grafana-alerting`. | **No** usar `minikube image load` directo sobre `:latest` (deja binario viejo). Con Collector caído vigilar pulso manual.<br>**Listo:** binario verificado y datos en Grafana. |

---

## 6. Comunicación, cierre y Postmortem

- **Mensajes estándar (`Comms` en hilo Discord):**
  - *Apertura:* `🔴 INC-AAAAMMDD-n · SEV<X> · Síntoma e impacto · Roles: IC/Técnico/Comms · Próxima update: HH:MM`
  - *Cierre:* `🟢 INC-AAAAMMDD-n · RESUELTO HH:MM · Causa · Cambios temporales aplicados · Postmortem (autor/fecha)`
- **Criterios de cierre (`IC`):** pulso OK 3 veces seguidas (c/1 min), `get deploy` completo (`2/2` apps, `1/1` BD), alerta en estado `Resolved`, `zero-downtime-test.sh` sin fallos, evidencia respaldada y cambios manuales revertidos o llevados al repositorio.
- **Postmortem:** obligatorio para **SEV1 y SEV2** dentro de los 3 días hábiles en `incidentes/INC-AAAAMMDD-n.md`. Estructura: *Resumen, Impacto (duración/rutas), Línea de tiempo, Causa raíz (5 porqués), Qué funcionó / Puntos ciegos* y tabla de **Action Items (Acción | Responsable | Fecha)** enfocada en procesos y sistema, nunca personas. Cada postmortem debe cerrar con al menos una mejora concreta (alerta nueva/calibrada, escenario de caos o ajuste a este plan).
