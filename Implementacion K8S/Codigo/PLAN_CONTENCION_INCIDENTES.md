# Plan de contención de incidentes - PharmaGo

Qué hacemos, quién lo hace y con qué comandos cuando PharmaGo se rompe o está por romperse.

Está pensado para leerse **con una alerta sonando**: frases cortas, pasos numerados y comandos que se copian y pegan. Sigue el marco de [gestión de incidentes de Atlassian](https://www.atlassian.com/incident-management/handbook), recortado a lo que tres personas pueden sostener de verdad.

> **¿Hay un incidente ahora mismo?** No leas todo. Andá a [Los primeros 5 minutos](#primeros-5-min) y después al playbook que te toque.

<a id="como-buscar"></a>
## Cómo encontrar algo rápido

- **Te llegó una alerta a Discord:** `Ctrl+F` y pegá el título tal cual (por ejemplo `Circuit breaker abierto`). Los playbooks llevan el mismo nombre que la alerta.
- **No hay alerta pero algo anda mal:** mirá la tabla de [síntomas](#indice).
- **Buscás un playbook por código:** todos empiezan con `PB-` (`PB-BD`, `PB-DEPLOY`, `PB-LOGIN`...).
- En VS Code, `Ctrl+Shift+O` lista los títulos del documento.

### Contenido

1. [Los primeros 5 minutos](#primeros-5-min)
2. [Índice: qué ves → qué playbook](#indice)
3. [Qué tan grave es (SEV)](#severidad)
4. [Quién hace qué, siendo tres](#roles)
5. [El recorrido de un incidente](#recorrido)
6. [Reglas de contención](#reglas)
7. [Kit de diagnóstico](#kit)
8. [Playbooks](#playbooks)
9. [Plantillas de mensajes](#plantillas)
10. [Cerrar y aprender (postmortem)](#postmortem)
11. [Puntos ciegos que ya conocemos](#puntos-ciegos)
12. [Antes de que pase: preparación y simulacros](#preparacion)

---

<a id="primeros-5-min"></a>
## 1. Los primeros 5 minutos

1. **Avisá que lo viste.** Respondé a la alerta en Discord: `La tomo, soy IM`. Si ya hay alguien, sumate y preguntá qué rol falta.
2. **Abrí un hilo** sobre el mensaje de la alerta con el nombre `INC-AAAAMMDD-n` (ej. `INC-20261112-1`). Todo lo del incidente se escribe ahí: ese hilo es la bitácora.
3. **Tomale el pulso al sistema** (30 segundos, comando en el [kit](#pulso)). Necesitás saber qué ven los usuarios, no solo qué dice la alerta.
4. **Poné una severidad** con la [tabla de SEV](#severidad). Si dudás entre dos, elegí la más grave: bajarla después no cuesta nada.
5. **Repartí roles** ([sección 4](#roles)) y mandá el [mensaje de apertura](#plantilla-apertura).
6. **Guardá evidencia antes de tocar nada** ([comando](#evidencia)). Es un minuto y después no se puede recuperar.
7. **Contené** con el playbook que corresponda. Primero que deje de doler; la causa raíz se busca después.

> Si estás solo: pasos 1, 3, 6 y 7. Escribí en el hilo lo que vas haciendo, aunque sean tres palabras. Los demás se suman cuando lleguen.

---

<a id="indice"></a>
## 2. Índice: qué ves → qué playbook

### Alertas de Grafana (llegan a Discord)

| Título de la alerta | SEV inicial | Playbook | Tarda en avisar* |
|---|---|---|---|
| Servicio sin réplicas disponibles | SEV1 | [PB-SERVICIO-CAIDO](#pb-servicio-caido) | ~2,5 min |
| Circuit breaker abierto | SEV1 si son varias tablas, SEV2 si es una | [PB-BD](#pb-bd) | 45-95 s |
| Tasa de errores 5xx anómala | SEV2 | [PB-5XX](#pb-5xx) | ~4-5 min |
| Disco del nodo casi lleno | SEV2 (pasa a SEV1 si baja del 10%) | [PB-DISCO](#pb-disco) | 5 min o más (sin medir) |
| Memoria de pod cerca del límite (riesgo de OOMKill) | SEV3 | [PB-MEMORIA](#pb-memoria) | 3 min o más (sin medir) |
| Saturación de requests en curso | SEV2 | [PB-TRAFICO](#pb-trafico) | 2 min o más (sin medir) |
| Latencia anómala respecto de la línea base | SEV2 si los usuarios lo notan, si no SEV3 | [PB-LATENCIA](#pb-latencia) | 3 min o más (sin medir) |
| Tráfico anómalo en el gateway (z-score) | SEV3 | [PB-TRAFICO](#pb-trafico) | ~4 min |
| Logins fallidos anómalos (posible fuerza bruta) | SEV3 (SEV1 si alguien logró entrar) | [PB-LOGIN](#pb-login) | ~2,5 min |
| CPU de pod cerca del límite | SEV3 | [PB-CPU](#pb-cpu) | **~7-10 min** |

\* Los valores con `~` se midieron con la [suite de caos](chaos-engineering/README.md). Los marcados "sin medir" son el mínimo que exige la regla de la alerta. En todos los casos: cuando llega la alerta, el problema ya lleva ese rato.

> La etiqueta `severity: critical / warning` de la alerta **no es** el SEV del incidente. La alerta dice qué tan fea es la métrica; el SEV dice cuánto le duele al usuario.

### Síntomas sin alerta

| Lo que pasa | Playbook |
|---|---|
| Acabo de desplegar y algo se rompió | [PB-DEPLOY](#pb-deploy) |
| Los usuarios dicen que anda lento y no saltó nada | [PB-LATENCIA](#pb-latencia), parte B |
| Los dashboards de Grafana están planos o sin datos | [PB-OTEL](#pb-otel) |
| No llega ninguna alerta a Discord hace días | [PB-OTEL](#pb-otel), parte B |
| Entraron a una cuenta ajena, o se filtró una clave o token | [PB-SEGURIDAD](#pb-seguridad) |
| No responde nada, ni `kubectl` | [PB-NODO](#pb-nodo) |
| Se cortó un experimento de caos y quedó algo raro | [PB-CAOS](#pb-caos) |
| Faltan datos, o hay datos corruptos o duplicados | [PB-DATOS](#pb-datos) |
| Las escrituras fallan pero las lecturas andan | [PB-BD](#pb-bd), caso C |

---

<a id="severidad"></a>
## 3. Qué tan grave es (SEV)

| Nivel | Qué significa en PharmaGo | Ejemplos | Cómo respondemos |
|---|---|---|---|
| **SEV1** | Los usuarios no pueden usar la app, o hay datos o cuentas en riesgo | La UI no carga. Nadie puede entrar. La BD está caída. Un servicio quedó sin réplicas. Se perdieron datos. Alguien entró a una cuenta ajena | Los tres, ya. Llamada de voz. Actualización cada 15 min |
| **SEV2** | Una función importante falla o anda mal, pero el resto sirve | Solo `/api/drug` da 503. Más de 5% de errores 5xx. Lentitud que los usuarios notan. Disco por llenarse | Dos personas como mínimo. Actualización cada 30 min |
| **SEV3** | Los usuarios no notan nada, pero perdimos margen o visibilidad | Una réplica caída (la otra atiende). CPU o memoria alta en un pod. Observabilidad caída. Fuerza bruta que el rate limit ya está frenando | Una persona. Se anota en el hilo. Se resuelve en el día |

**Subí de nivel** si pasa el tiempo y no mejora, si aparece un segundo síntoma, o si no entendés qué está pasando. No entender ya es motivo suficiente.

---

<a id="roles"></a>
## 4. Quién hace qué, siendo tres

Atlassian define tres roles centrales y nosotros somos tres: uno cada uno. Los roles **no son fijos por persona**, se reparten en cada incidente.

| Rol | Qué hace | Qué NO hace |
|---|---|---|
| **IM** (responsable del incidente) | Decide. Pone el SEV, reparte roles, aprueba las acciones riesgosas, decide cuándo está contenido y cuándo se cierra | No se pone a investigar. Si el IM tiene las manos en el teclado, nadie está mirando el conjunto |
| **Técnico** (Tech Lead) | Diagnostica y ejecuta la contención. Dice en voz alta qué va a hacer antes de hacerlo | No decide solo las acciones de la lista "pedir OK al IM" |
| **Comunicación** | Lleva la bitácora en el hilo con hora, manda las actualizaciones, mide el impacto desde afuera (el [pulso](#pulso)) y es el segundo par de ojos del Técnico | No se queda callado si ve algo raro |

**Cómo se reparten:**

- **IM:** quien primero escribe `La tomo`. Sigue siéndolo hasta que diga explícitamente `Fulano, sos IM` y Fulano conteste que sí.
- **Técnico:** quien tiene el cluster corriendo. Minikube es local, así que solo esa persona puede ejecutar `kubectl`. Si justo es la misma que tomó el incidente, le pasa el rol de IM a otro.
- **Comunicación:** quien queda.

**Si no estamos los tres:**

| Disponibles | Cómo queda |
|---|---|
| 2 | Uno es Técnico. El otro es IM y Comunicación a la vez |
| 1 | Sos los tres. Orden: avisar en el hilo → evidencia → contener. La bitácora son notas de una línea |

**Cuándo pedir ayuda** (el "escalar, escalar, escalar" de Atlassian):

- SEV1 y nadie contestó en el hilo en 10 minutos → llamá por teléfono.
- El Técnico lleva 20 minutos sin avanzar → el IM suma a la tercera persona, aunque sea "para mirar".
- Hay que tomar una decisión que excede al equipo (borrar datos, avisar a terceros) → el IM contacta al responsable externo.

### Equipo y contactos

| Persona (GitHub) | Teléfono | ¿Tiene el cluster local? |
|---|---|---|
| jirabedra | _completar_ | _completar_ |
| AndresCarsin23 | _completar_ | _completar_ |
| agustinpuente04 | _completar_ | _completar_ |
| Responsable externo (docente / cliente) | _completar_ | - |

---

<a id="recorrido"></a>
## 5. El recorrido de un incidente

Las etapas de Atlassian, con lo que significan acá:

| Etapa | Qué hacemos | Quién |
|---|---|---|
| **1. Detectar** | Llega la alerta a Discord, o alguien avisa. La idea es enterarnos antes que el usuario | Cualquiera |
| **2. Abrir** | `La tomo` + hilo `INC-AAAAMMDD-n` | IM |
| **3. Evaluar** | Pulso del sistema, SEV, evidencia | IM + Técnico |
| **4. Comunicar** | Mensaje de apertura. Después, actualizaciones a horario fijo, haya o no novedades | Comunicación |
| **5. Escalar y delegar** | Sumar gente, repartir tareas con nombre: "Fulano, mirá Kibana" | IM |
| **6. Contener** | Playbook. Un cambio a la vez, anunciado antes | Técnico |
| **7. Resolver** | Verificar con los [criterios de cierre](#cierre), mensaje final | IM |
| **8. Aprender** | Postmortem sin culpables, con acciones que tengan dueño y fecha | Los tres |

**Contenido** no es lo mismo que **resuelto**. Contenido: el usuario ya no sufre (aunque sea con un parche). Resuelto: además entendimos por qué pasó y no va a volver en cinco minutos.

---

<a id="reglas"></a>
## 6. Reglas de contención

1. **Primero contener, después entender.** Si reiniciar un pod devuelve el servicio, se reinicia y se investiga con la evidencia guardada.
2. **Evidencia antes de tocar.** Los logs del contenedor anterior se pierden cuando el pod se recrea.
3. **Un cambio a la vez, y avisado.** `Voy a escalar pharmacy a 3` en el hilo, después lo ejecutás, después contás qué pasó. Dos cambios juntos y ya no sabés cuál funcionó.
4. **Preferí lo reversible.** Escalar, reiniciar una réplica o cambiar una variable se deshacen. Borrar no.
5. **Con la BD caída, no reinicies las apps.** No ayuda: se recuperan solas en 15-45 s cuando la BD vuelve. Y como aplican migraciones al arrancar, un pod reiniciado sin BD probablemente no levante.
6. **De a una réplica.** Cada app tiene 2. Borrar una no se nota; borrar las dos juntas es una caída que provocamos nosotros.
7. **Lo que cambies a mano se pierde en el próximo `kubectl apply`.** Anotá cada cambio de emergencia en el hilo: al cerrar hay que llevarlo al repo o revertirlo.

### Qué se puede hacer sin preguntar

| Acción | ¿Se puede? |
|---|---|
| Borrar **un** pod de UI, gateway, users o pharmacy | Sí |
| `kubectl rollout restart` de una app (cero downtime, verificado) | Sí |
| Escalar una app hacia arriba | Sí |
| `./chaos.sh cleanup` | Sí, es idempotente |
| Reiniciar Grafana, Kibana, Elasticsearch, Prometheus u OTel | Sí, el usuario no lo nota |
| Reiniciar `pharmago-db` | **OK del IM.** Son 1-2 minutos sin BD |
| Matar sesiones en SQL Server (`KILL`) | **OK del IM** |
| Cambiar límites de rate limit o de recursos | **OK del IM** |
| Borrar índices de Elasticsearch | **OK del IM.** Son el único registro de auditoría |
| Escalar una app a 0 | **OK del IM.** Es una caída voluntaria |
| `minikube delete`, `./cleanup.sh`, borrar PV/PVC, tocar `/mnt/data/sql` | **Nunca durante un incidente.** Se lleva la base de datos |
| Correr experimentos de caos | **Nunca durante un incidente** |

---

<a id="kit"></a>
## 7. Kit de diagnóstico

Todo se ejecuta en **Git Bash**, desde `Implementacion K8S/Codigo`.

### Preparar la terminal

```bash
export MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*'   # Git Bash: si falta, kubectl exec rompe las rutas tipo /opt/...
(cd k8s && ./port-forward.sh)                       # abre Grafana, Kibana, Prometheus y la UI en 127.0.0.1
```

| Herramienta | Dirección | Para qué |
|---|---|---|
| UI | http://127.0.0.1:4200 | Lo que ve el usuario |
| Grafana | http://127.0.0.1:3000 (admin / admin) | Alertas: `/alerting/list`. Dashboards: `/d/pharmago-overview`, `/d/pharmago-app`, `/d/pharmago-infra` |
| Kibana | http://127.0.0.1:5601 | Logs. Data view `pharmago-logs` |
| Prometheus | http://127.0.0.1:9090 | Consultas sueltas |

> El port-forward se cuelga de **un solo pod**. Si ese pod se reemplaza, el túnel muere aunque el servicio esté sano. Antes de concluir "está todo caído", relanzá `./port-forward.sh`.

<a id="pulso"></a>
### Pulso: qué ve el usuario

```bash
URL=http://127.0.0.1:4200
for p in / /api/drug /api/pharmacy /api/users; do
  printf '%-14s %s\n' "$p" "$(curl -s -o /dev/null -w '%{http_code}  %{time_total}s' --max-time 6 "$URL$p")"
done
```

| Resultado | Lectura |
|---|---|
| `200`, o `401` en `/api/users` | Sano. El 401 es lo esperado sin sesión: significa que users respondió |
| `503` en milisegundos en `/api/drug` y `/api/pharmacy` | La BD no responde y el circuit breaker está cortando → [PB-BD](#pb-bd) |
| `503` solo en `/api/drug` | Problema en una tabla puntual → [PB-BD](#pb-bd), caso B |
| `502` / `504` en `/api/*` y `/` anda | El gateway o el servicio de atrás no está → [PB-SERVICIO-CAIDO](#pb-servicio-caido) |
| `429` | Rate limit. Si es tráfico legítimo → [PB-TRAFICO](#pb-trafico) |
| `000` en todo | Túnel caído, UI caída o nodo caído. Relanzá el port-forward; si sigue → [PB-NODO](#pb-nodo) |
| `200` pero lento | [PB-LATENCIA](#pb-latencia) |

Qué recorre cada ruta:

```
/                                      nginx (pharmago-ui)
/api/login /users /roles /invitations  nginx → gateway → users-service    → pharmago-db
/api/drug /pharmacy /purchases
/api/stockrequest /unitmeasures
/api/presentations /export             nginx → gateway → pharmacy-service → pharmago-db
```

`/api/users` sin token responde 401 **sin consultar la BD**: no sirve para saber si la BD anda. Para eso usá `/api/drug`.

### Estado del cluster

```bash
kubectl -n pharmago get deploy                       # las apps deben estar 2/2; la BD 1/1
kubectl -n pharmago get pods -o wide                 # mirá STATUS, RESTARTS y AGE (¿algo se recreó hace poco?)
kubectl -n pharmago get events --sort-by=.lastTimestamp | tail -25
kubectl -n pharmago describe pod <pod>               # por qué no arranca: mirá "Events" y "Last State"
kubectl -n pharmago logs <pod> --tail=100            # qué dice ahora
kubectl -n pharmago logs <pod> --previous --tail=100 # qué dijo antes de reiniciarse
```

<a id="evidencia"></a>
### Guardar evidencia (antes de tocar)

```bash
D=~/inc-$(date +%Y%m%d-%H%M); mkdir -p "$D"
kubectl -n pharmago get pods,deploy,pdb -o wide           > "$D/estado.txt"
kubectl -n pharmago get events --sort-by=.lastTimestamp   > "$D/eventos.txt"
for a in pharmago-ui pharmago-api-gateway pharmago-users-service pharmago-pharmacy-service pharmago-db; do
  kubectl -n pharmago logs -l app=$a --all-containers --prefix --tail=500 > "$D/$a.log" 2>&1
done
echo "Evidencia en $D"
```

Si un pod se reinició, sumá `kubectl -n pharmago logs <pod> --previous > "$D/<pod>-previo.log"`.

### Buscar en los logs (Kibana → Discover)

| Quiero ver | Consulta KQL |
|---|---|
| Errores de un servicio | `LogLevel : "Error" and kubernetes.labels.app : "pharmago-pharmacy-service"` |
| Una request de punta a punta | `Scopes.correlation_id : "<id>"` |
| Logins fallidos | `Scopes.pharma_biz : "login_fail"` |
| Todo lo que hizo un usuario | `Scopes.user_name : "<usuario>"` |
| Requests lentas | `State.ElapsedMs > 500` |
| Fallos de BD | `Message : "Fallo de BD"` |

Más detalle en [tutoriales/como-revisar-logs-kibana.md](tutoriales/como-revisar-logs-kibana.md).

<a id="pgsql"></a>
### Consultar la base de datos

Es la misma función que usa la suite de caos. Pegala una vez por terminal:

```bash
pgsql() {  # pgsql "<consulta>" [base]   (por defecto PharmaDb)
  local pw; pw="$(kubectl get secret db-secret -n pharmago -o jsonpath='{.data.SA_PASSWORD}' | base64 -d)"
  kubectl exec -n pharmago deploy/pharmago-db -c pharmago-db -- sh -c \
    "t=/opt/mssql-tools18/bin/sqlcmd; fl=-C; [ -x \$t ] || { t=/opt/mssql-tools/bin/sqlcmd; fl=; }; \$t -S localhost -U sa -P '$pw' \$fl -d ${2:-PharmaDb} -W -b -Q \"$1\""
}
pgsql "SELECT 1" master        # ¿la BD contesta?
```

Dentro de la consulta usá comillas simples. Las dobles las usa la función.

---

<a id="playbooks"></a>
## 8. Playbooks

Todos tienen la misma forma: **qué significa → confirmar → contener → no hagas → listo cuando**.

<a id="pb-servicio-caido"></a>
### PB-SERVICIO-CAIDO · Servicio sin réplicas disponibles

**Buscar por:** `no responde`, `0/2`, `CrashLoopBackOff`, `Pending`, `502`

**Qué significa:** Prometheus no puede leer `api-gateway`, `users-service` o `pharmacy-service`. No queda ninguna réplica atendiendo: todas las rutas de ese servicio están caídas.

**Confirmar:**

```bash
kubectl -n pharmago get deploy
kubectl -n pharmago get pods -l app=pharmago-pharmacy-service     # cambiá el nombre por el que figure en la alerta
```

**Contener, según lo que veas:**

| Ves | Qué hacer |
|---|---|
| `0/0`: lo escalaron a cero | `kubectl -n pharmago scale deploy/<svc> --replicas=2`. Si venía de un experimento: [PB-CAOS](#pb-caos) |
| `CrashLoopBackOff` o `Error` | `kubectl -n pharmago logs <pod> --previous --tail=50`. Si hubo un deploy reciente → [PB-DEPLOY](#pb-deploy). Si el error es de conexión a la BD → [PB-BD](#pb-bd) |
| `Pending` | `kubectl -n pharmago describe pod <pod>`. Suele ser falta de memoria en el nodo o la etiqueta: `kubectl label nodes minikube node-type=all --overwrite` |
| `ErrImageNeverPull` | La imagen no está en el nodo: `minikube image ls \| grep pharmago` y después `(cd k8s && ./build-images.sh)` |
| `OOMKilled` en `describe` | [PB-MEMORIA](#pb-memoria) |

**No hagas:** borrar el Deployment para "recrearlo". Escalar o reiniciar alcanza y no se pierde la configuración.

**Listo cuando:** `kubectl -n pharmago rollout status deploy/<svc>` termina bien y el [pulso](#pulso) da 200. Referencia: tras restaurar las réplicas el servicio vuelve en menos de 90 s (escenario D2).

**Ojo:** el PodDisruptionBudget no protege contra un escalado a 0, solo contra drains y evictions.

---

<a id="pb-bd"></a>
### PB-BD · Circuit breaker abierto

**Buscar por:** `breaker`, `503`, `db:Drug`, `BD caída`, `SQL Server`, `pharmago-db`, `bloqueo`, `1105`

**Qué significa:** un servicio dejó de llamar a la base porque falló 5 veces seguidas. Mientras dure, esas rutas contestan **503 en milisegundos** en vez de colgarse. Es una protección funcionando: el problema real está en la BD.

La BD es **una sola instancia** (SQL Server Express). Es nuestro único punto de falla sin redundancia.

**Confirmar:**

```bash
kubectl -n pharmago get pods -l app=pharmago-db
pgsql "SELECT 1" master
```

En Grafana → *PharmaGo - Negocio y Runtime* → panel **Estado del circuit breaker**: fijate **cuántos** `target` están abiertos. La alerta también lo dice (`cortó las llamadas a db:Drug`).

**Caso A · Varios targets abiertos, o el pod de la BD no está `Running`: la BD está caída o inalcanzable (SEV1)**

1. Mirá por qué: `kubectl -n pharmago describe pod -l app=pharmago-db` y `kubectl -n pharmago logs -l app=pharmago-db --tail=50`.
2. Si está en `0/0`: `kubectl -n pharmago scale deploy/pharmago-db --replicas=1`.
3. Si se reinicia sola: revisá `OOMKilled` (límite 1,5 Gi) y disco lleno ([PB-DISCO](#pb-disco)).
4. Si está `Running` y `SELECT 1` contesta, pero las apps no llegan: es la red. `kubectl -n pharmago get svc,endpoints pharmago-db` y, si hubo pruebas de caos, [PB-CAOS](#pb-caos).
5. Último recurso, **con OK del IM**: `kubectl -n pharmago rollout restart deploy/pharmago-db`. Son 1-2 minutos sin BD. Los datos persisten (verificado en D3).

**Caso B · Un solo target abierto y la BD responde: hay una tabla trabada o consultas pesadas (SEV2)**

El caso típico es un `ALTER TABLE` o una migración larga sobre una tabla en uso. Buscá quién tiene el bloqueo:

```bash
# quién tiene un bloqueo de esquema, y sobre qué tabla
pgsql "SELECT request_session_id AS sesion, request_mode, OBJECT_NAME(resource_associated_entity_id) AS tabla FROM sys.dm_tran_locks WHERE resource_type='OBJECT' AND request_mode='Sch-M'"

# sesiones con transacciones abiertas
pgsql "SELECT s.session_id, s.program_name, s.host_name, s.login_time FROM sys.dm_tran_session_transactions t JOIN sys.dm_exec_sessions s ON s.session_id=t.session_id WHERE s.is_user_process=1"

# consultas que más CPU consumen ahora
pgsql "SELECT TOP 10 r.session_id, r.status, r.cpu_time, r.total_elapsed_time/1000 AS seg, SUBSTRING(t.text,1,100) AS consulta FROM sys.dm_exec_requests r CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t WHERE r.session_id<>@@SPID ORDER BY r.cpu_time DESC"
```

Con la sesión identificada y el **OK del IM**: `pgsql "KILL <sesion>" master`. SQL Server deshace su transacción.

Si la sesión es de un deploy que está migrando, matarla aborta la migración y el pod va a reintentar al reiniciarse: seguí con [PB-DEPLOY](#pb-deploy).

**Caso C · Las escrituras fallan y las lecturas andan: la BD se quedó sin espacio**

`pgsql "EXEC sp_helpfile"` muestra el tamaño y el tope del archivo de datos. Si quedó un tope de un experimento: [PB-CAOS](#pb-caos). SQL Server Express admite como máximo 10 GB por base.

**No hagas:**

- Reiniciar users o pharmacy. Se recuperan solos (15-45 s) cuando la BD vuelve.
- Tocar `/mnt/data/sql`, los PV o correr `./cleanup.sh`.
- Esperar a que Kubernetes lo arregle. `/health` no chequea la BD: los pods siguen `Ready` y recibiendo tráfico que falla.

**Listo cuando:** el panel del breaker vuelve a 0, `/api/drug` da 200 y la cantidad de registros es la esperada. El mensaje de la alerta dice que el breaker queda abierto 30 s; en la BD son 15 s.

**Detalle:** [Backend/RESILIENCIA_BD.md](Backend/RESILIENCIA_BD.md). **Practicar con:** `./chaos.sh run D3`, `N3` o `C4`.

---

<a id="pb-5xx"></a>
### PB-5XX · Tasa de errores 5xx anómala

**Buscar por:** `5xx`, `500`, `errores de servidor`, `Exception`

**Qué significa:** más del 5% de las respuestas de un servicio son errores, sostenido 2 minutos. La alerta dice qué servicio (`PharmaGo.PharmacyService: 12.3% de respuestas 5xx`).

**Confirmar y contener, en este orden:**

1. **¿Está abierto el breaker?** Si hay alerta de *Circuit breaker abierto* o el pulso da 503 rápidos → es la BD: [PB-BD](#pb-bd). Suele llegar antes que esta.
2. **¿Hubo un deploy?** `kubectl -n pharmago get pods` y mirá `AGE`. Si los pods del servicio tienen minutos → [PB-DEPLOY](#pb-deploy).
3. **¿Qué endpoint?** Grafana → *PharmaGo - Overview* → **Tasa de Error por Endpoint (%)**.
4. **¿Qué error?** Kibana: `LogLevel : "Error" and kubernetes.labels.app : "<app>"`. Abrí un documento y mirá `Exception`. Con `Scopes.correlation_id` seguís esa request entre gateway y servicio.
5. **¿Es una sola réplica?** Si todos los errores tienen el mismo `kubernetes.pod_name`, borrá ese pod: `kubectl -n pharmago delete pod <pod>`. La otra réplica sigue atendiendo.

**No hagas:** reiniciar todo "por las dudas". Perdés los logs del contenedor y, si es la BD, no cambia nada.

**Listo cuando:** el panel de errores vuelve a su nivel normal y Discord recibe el `Resolved`.

---

<a id="pb-disco"></a>
### PB-DISCO · Disco del nodo casi lleno

**Buscar por:** `disco`, `espacio`, `storage`, `evicted`, `no space left`

**Qué significa:** queda menos del 15% libre en el nodo. Ahí escriben SQL Server, Elasticsearch y Prometheus. **Al 10% el kubelet empieza a desalojar pods:** en ese punto pasa a SEV1.

**Confirmar:**

```bash
minikube ssh -- df -h
minikube ssh -- "sudo du -sh /mnt/data/*"
minikube ssh -- docker system df
```

**Contener, de lo más seguro a lo más delicado:**

1. **Restos de experimentos:** `(cd chaos-engineering && ./chaos.sh cleanup)`. Borra archivos de relleno, la tabla `chaos_fill` y los logs de la inundación.
2. **Imágenes viejas:** `minikube ssh -- docker image prune -f`. Cada vez que recargamos una imagen `:latest`, la anterior queda huérfana ocupando lugar.
3. **Logs viejos en Elasticsearch** (con OK del IM, son nuestra auditoría):
   ```bash
   kubectl -n pharmago exec deploy/elasticsearch -c elasticsearch -- curl -s 'localhost:9200/_cat/indices/pharmago-logs-*?v&s=index'
   kubectl -n pharmago exec deploy/elasticsearch -c elasticsearch -- curl -s -X DELETE 'localhost:9200/pharmago-logs-2026.10.01'
   ```
   Borrá de a un índice, por nombre exacto, empezando por el más viejo.

**No hagas:** borrar nada dentro de `/mnt/data/sql`. Tampoco borres datos de Prometheus (guarda 30 días) apurado.

**Listo cuando:** `df -h` muestra más de 20% libre. Dejá margen: si quedás en 16%, la alerta vuelve mañana.

---

<a id="pb-memoria"></a>
### PB-MEMORIA · Memoria de pod cerca del límite (riesgo de OOMKill)

**Buscar por:** `OOMKilled`, `memoria`, `memory leak`, `RESTARTS`

**Qué significa:** un pod pasó el 85% de su límite. Al 100% el kernel lo mata y Kubernetes lo reinicia. Con 2 réplicas, perder una no se nota (escenario M2), pero es un aviso.

**Confirmar:** Grafana → *PharmaGo - Infra* → **Memoria por pod - % del límite**. Mirá si sube **un** pod o **todos** los del servicio.

```bash
kubectl -n pharmago get pods                                  # ¿ya hay RESTARTS?
kubectl -n pharmago describe pod <pod> | grep -A5 "Last State"
```

**Contener:**

| Situación | Qué hacer |
|---|---|
| Una réplica de una app | Guardá sus logs y borrala: `kubectl -n pharmago delete pod <pod>`. Esperá a que la nueva esté `Ready` antes de tocar la otra |
| Las dos réplicas suben parejas | Es una pérdida de memoria o más carga. Parche (con OK del IM): `kubectl -n pharmago set resources deploy/<svc> --limits=memory=512Mi`. Antes mirá si el nodo tiene lugar: `kubectl describe node minikube \| grep -A8 "Allocated resources"`. Si empezó tras un deploy → [PB-DEPLOY](#pb-deploy) |
| Elasticsearch, Kibana, Grafana | Reinicialo sin miedo, el usuario no lo nota: `kubectl -n pharmago rollout restart deploy/<nombre>` |
| `pharmago-db` | Decide el IM. Un reinicio elegido (1-2 min) es mejor que un OOMKill en cualquier momento |

**Listo cuando:** el pod está por debajo del 70% y estable durante 15 minutos. Si vuelve a subir, hay una pérdida de memoria: va al postmortem.

---

<a id="pb-cpu"></a>
### PB-CPU · CPU de pod cerca del límite

**Buscar por:** `CPU`, `throttling`, `lento`

**Qué significa:** un pod usa más del 80% de su límite de CPU y Kubernetes lo está frenando. **Esta alerta llega tarde:** necesita unos 10 minutos de saturación sostenida.

**Confirmar:** Grafana → *PharmaGo - Infra* → **CPU por pod - % del límite**. ¿Un pod o todos los del servicio?

**Contener:**

| Situación | Qué hacer |
|---|---|
| Una sola réplica | Algo se colgó en ese pod. Borralo: `kubectl -n pharmago delete pod <pod>`. La otra réplica sostiene el servicio (escenario C1) |
| Todas las réplicas de un servicio | Es carga. Mirá el tráfico en *Overview* → **Peticiones por Segundo por Endpoint**. Si hay un pico → [PB-TRAFICO](#pb-trafico). Para aguantar: `kubectl -n pharmago scale deploy/<svc> --replicas=3` |
| `pharmago-db` | Consultas pesadas → [PB-BD](#pb-bd), caso B |

**Listo cuando:** baja del 60%. Si escalaste a 3, anotalo: hay que volver a 2 o llevar el cambio al repo.

---

<a id="pb-trafico"></a>
### PB-TRAFICO · Tráfico anómalo en el gateway (z-score) · Saturación de requests en curso

**Buscar por:** `429`, `rate limit`, `ráfaga`, `ataque`, `DDoS`, `in flight`, `Retry-After`

**Qué significa:**

- *Tráfico anómalo:* entra mucho más (o mucho menos) tráfico que en la última hora. Puede ser un pico real, un cliente en loop o un ataque.
- *Saturación de requests en curso:* una réplica tiene más de 20 requests abiertas a la vez. Entran más rápido de lo que salen: anticipa timeouts.

**Confirmar:** Grafana → *PharmaGo - Overview* → **Rate Limit Errors (429) por Endpoint** y **Distribución de Status Codes (%)**.

**Contener:**

1. **El exceso sale como 429 y no hay 5xx ni lentitud:** el rate limit está haciendo su trabajo. SEV3, mirá 10 minutos y dejá nota. Los usuarios con sesión tienen su propio cupo y no se ven afectados (escenario R4).
2. **Es tráfico legítimo y hay usuarios reales recibiendo 429:** subí el cupo anónimo (con OK del IM):
   ```bash
   kubectl -n pharmago set env deploy/pharmago-api-gateway RateLimiting__Anonymous__Capacity=300 RateLimiting__Anonymous__RefillPerMinute=300
   ```
3. **Es un ataque y está degradando a los legítimos:** bajalo.
   ```bash
   kubectl -n pharmago set env deploy/pharmago-api-gateway RateLimiting__Anonymous__Capacity=30 RateLimiting__Anonymous__RefillPerMinute=30
   ```
4. **Saturación en una sola réplica:** borrá ese pod. **En todas:** algo de atrás está lento. Revisá la BD ([PB-BD](#pb-bd)) y el CPU ([PB-CPU](#pb-cpu)).

Para volver a los valores por defecto (100 / 100 por minuto):

```bash
kubectl -n pharmago set env deploy/pharmago-api-gateway RateLimiting__Anonymous__Capacity- RateLimiting__Anonymous__RefillPerMinute-
```

**Tené en cuenta:**

- `set env` reinicia el gateway de forma ordenada, sin downtime. Pero el estado del rate limit vive en memoria: **se borran los cupos y los bloqueos de login en curso**.
- Con 2 réplicas, el límite efectivo puede llegar al doble del configurado.
- **No tenemos bloqueo por IP.** La única palanca es el rate limit.
- Una segunda ráfaga dentro de la misma hora puede **no** disparar la alerta: la primera ya infló la línea base.

**Listo cuando:** los 429 vuelven a cero y la latencia está normal. Anotá si dejaste límites cambiados.

**Detalle:** [Backend/RATE_LIMITING_GUIDE.md](Backend/RATE_LIMITING_GUIDE.md). **Practicar con:** `./chaos.sh run R1`, `R4`.

---

<a id="pb-login"></a>
### PB-LOGIN · Logins fallidos anómalos (posible fuerza bruta)

**Buscar por:** `fuerza bruta`, `login_fail`, `spraying`, `contraseña`, `cuenta bloqueada`

**Qué significa:** más de 5 logins fallidos por minuto. Una caída de la BD **no** cuenta como login fallido, así que esta alerta habla de credenciales de verdad.

**Confirmar** (Kibana, últimos 15 minutos):

```
Scopes.pharma_biz : "login_fail"
```

Mirá `Scopes.user_name`: ¿siempre la misma cuenta (ataque dirigido) o muchas distintas (prueban claves comunes contra todas)?

**Contener:**

1. **Lo primero: ¿lograron entrar?**
   ```
   Scopes.pharma_biz : "login_success" and Scopes.user_name : "<cuenta atacada>"
   ```
   Si hay un login exitoso que el dueño no reconoce → **SEV1**, seguí en [PB-SEGURIDAD](#pb-seguridad).
2. **Si no entraron,** la defensa ya está actuando: bloqueo por cuenta que arranca en 30 s y se duplica hasta 15 min, y 10 fallos por minuto por IP. El dueño legítimo sigue pudiendo entrar desde su equipo habitual (escenario R2).
3. **Si sigue durante más de 30 minutos,** endurecé (con OK del IM):
   ```bash
   kubectl -n pharmago set env deploy/pharmago-api-gateway RateLimiting__Login__FailedPerIp__Capacity=3 RateLimiting__Login__FailedPerIp__RefillPerMinute=3
   ```
   Al reiniciarse el gateway se pierden los bloqueos en curso: el atacante recupera unos intentos. Hacelo una vez, no varias.
4. **Avisale al dueño** de la cuenta atacada. Si la clave es débil, que la cambie ya.

**Tené en cuenta:** las claves se guardan en texto plano y el login distingue "usuario no existe" (404) de "clave incorrecta" (400). Un atacante puede averiguar qué cuentas existen. Es una deuda conocida ([sección 11](#puntos-ciegos)).

**Listo cuando:** la tasa baja de 5 por minuto y confirmaste que no hubo ningún ingreso indebido.

**Practicar con:** `./chaos.sh run R2`.

---

<a id="pb-latencia"></a>
### PB-LATENCIA · Latencia anómala respecto de la línea base

**Buscar por:** `lento`, `latencia`, `timeout`, `tarda`, `p95`

**Parte A · Saltó la alerta**

**Qué significa:** un servicio tarda 3 veces más que su promedio de la última hora (y más de 200 ms). Mide el tiempo **dentro del servidor**.

Revisá en este orden:

1. **CPU frenada:** *Infra* → **CPU por pod - % del límite** → [PB-CPU](#pb-cpu).
2. **BD lenta:** [PB-BD](#pb-bd), caso B, la consulta de "más CPU consumen".
3. **Deploy reciente:** [PB-DEPLOY](#pb-deploy).
4. **Qué endpoint:** *Overview* → **Latencia Promedio por Endpoint**, y en Kibana `State.ElapsedMs > 500`.
5. **Runtime:** *Negocio y Runtime* → **Thread pool: hilos y cola** y **Recolecciones del GC por segundo**.

**Parte B · Los usuarios se quejan y no saltó nada**

Es un punto ciego conocido: si la lentitud está **en la red** y no en el servidor, la alerta no la ve. Medido en N1: los clientes veían 586 ms y la métrica del servidor seguía en ~0.

Medí desde afuera:

```bash
for i in $(seq 1 20); do curl -s -o /dev/null -w '%{time_total}\n' --max-time 10 "$URL/api/users"; sleep 0.5; done
```

Si desde afuera es lento y en Grafana no:

1. ¿Quedaron reglas de red de un experimento? → [PB-CAOS](#pb-caos).
2. Reemplazá los pods del servicio afectado, de a uno: `kubectl -n pharmago rollout restart deploy/<svc>`. Las reglas de red viven dentro del pod y se van con él.
3. Si usás el túnel de `minikube service` en Windows: se satura cerca de las 100 conexiones y da timeouts que **no son** de la app.

**Listo cuando:** la medición desde afuera vuelve a decenas de milisegundos.

---

<a id="pb-deploy"></a>
### PB-DEPLOY · Un despliegue rompió algo

**Buscar por:** `rollback`, `rollout`, `deploy`, `imagen`, `latest`, `migración`, `volver atrás`

**Lo que tenés que saber antes de tocar:**

> Nuestras imágenes usan la etiqueta `:latest` con `imagePullPolicy: Never`. Por eso **`kubectl rollout undo` no devuelve el código anterior**: la etiqueta sigue apuntando al binario nuevo. `undo` solo revierte cambios del manifiesto (variables, recursos, réplicas). Para volver al código anterior hay que **reconstruir la imagen desde un commit bueno**.

**Caso A · El rollout está trabado: los pods nuevos no llegan a `Ready`**

Los usuarios **no lo notan**. Con `maxUnavailable: 0`, los pods viejos siguen atendiendo hasta que los nuevos estén sanos.

```bash
kubectl -n pharmago rollout status deploy/<svc> --timeout=10s
kubectl -n pharmago logs <pod-nuevo> --tail=50
kubectl -n pharmago rollout undo deploy/<svc>          # cancela el rollout trabado; quedan los pods viejos
```

Ojo: los pods viejos siguen con el binario bueno **solo mientras no se reinicien**. Hacé igual el caso B con calma.

**Caso B · El rollout terminó y la versión nueva funciona mal**

```bash
git log --oneline -10                                   # elegí el último commit bueno
git worktree add ~/pharmago-rollback <commit-bueno>     # copia aparte: no toca tu rama ni tus cambios

cd ~/pharmago-rollback/"Implementacion K8S/Codigo/Backend"
docker build -f PharmaGo.PharmacyService/Dockerfile -t pharmago-pharmacy-service:latest .
docker save pharmago-pharmacy-service:latest | minikube image load -     # así sí reemplaza la imagen

kubectl -n pharmago rollout restart deploy/pharmago-pharmacy-service
kubectl -n pharmago rollout status  deploy/pharmago-pharmacy-service

cd - && git worktree remove ~/pharmago-rollback         # limpiar al terminar
```

Para users o gateway, cambiá `PharmacyService` / `pharmacy-service`. La UI se construye desde `Frontend` con `docker build -f Dockerfile -t pharmago-ui:latest .`

Se usa una copia aparte (`worktree`) a propósito: si hicieras `git checkout` a un commit viejo, este mismo documento podría desaparecer de tu editor en pleno incidente.

La reconstrucción lleva varios minutos. **Mientras tanto,** si el daño es grave, el IM puede decidir escalar ese servicio a 0: mejor un 502 claro que datos mal escritos.

**Caso C · El deploy traía una migración de base de datos**

- Las apps migran solas al arrancar. Un `ALTER TABLE` largo sobre una tabla en uso traba esa tabla → [PB-BD](#pb-bd), caso B.
- **Volver el código atrás no deshace la migración.** Si el código viejo no funciona con el esquema nuevo, no hay rollback rápido: se arregla hacia adelante.

**No hagas:** confiar en `minikube image load <imagen>` a secas. Con la misma etiqueta **no reemplaza nada** y no avisa.

**Listo cuando:** el pulso da 200 y verificaste que el pod corre el código que creés. Cómo comprobarlo: [k8s/README-k8s.md](k8s/README-k8s.md), sección "Actualizar un componente sin downtime".

---

<a id="pb-otel"></a>
### PB-OTEL · Nos quedamos ciegos (observabilidad caída)

**Buscar por:** `sin datos`, `No data`, `dashboards vacíos`, `OTel`, `collector`, `Grafana`, `webhook`, `no llegan alertas`

**Qué significa:** los usuarios no notan nada, pero **nosotros dejamos de ver**. Es SEV3 con una condición: mientras dure, alguien mira el [pulso](#pulso) a mano cada tanto.

**Parte A · Los dashboards de aplicación están planos**

Casi seguro es el OTel Collector. Si cae, **no avisa**: las alertas están configuradas para tratar "sin datos" como "todo bien".

| Con el collector caído | Alertas |
|---|---|
| **Quedan mudas** | 5xx, Latencia, Tráfico, Logins fallidos, Circuit breaker, Saturación |
| **Siguen funcionando** | Servicio sin réplicas, CPU, Memoria, Disco |

```bash
kubectl -n pharmago get pods -l app=otlp-collector
kubectl -n pharmago logs -l app=otlp-collector --tail=50
kubectl -n pharmago rollout restart deploy/otlp-collector
```

En Prometheus, `up{job="otlp-collector"}` debe dar 1.

**Parte B · No llega ninguna alerta a Discord**

Si se desplegó sin la variable `ALERT_WEBHOOK_URL`, las alertas se evalúan pero **no se envían**. Comprobalo:

```bash
kubectl -n pharmago get secret grafana-alerting -o jsonpath='{.data.webhook_url}' | base64 -d; echo
```

Si muestra `http://127.0.0.1:9/disabled`, está apagado. Para encenderlo:

```bash
export ALERT_WEBHOOK_URL='https://discord.com/api/webhooks/...'
kubectl create secret generic grafana-alerting -n pharmago --from-literal=webhook_url="$ALERT_WEBHOOK_URL" --dry-run=client -o yaml | kubectl apply -f -
kubectl -n pharmago rollout restart deploy/grafana
```

**Parte C · No hay logs en Kibana**

```bash
kubectl -n pharmago get pods -l k8s-app=fluent-bit-logging
kubectl -n pharmago get pods -l app=elasticsearch
```

Si Elasticsearch se reinicia por memoria: [k8s/README-k8s.md](k8s/README-k8s.md), sección "Elasticsearch OOMKilled".

**Listo cuando:** los paneles vuelven a tener datos y probaste que una alerta llega de punta a punta (por ejemplo con `./chaos.sh run D2`).

---

<a id="pb-seguridad"></a>
### PB-SEGURIDAD · Cuenta comprometida o secreto filtrado

**Buscar por:** `hackeo`, `acceso indebido`, `token`, `sesión`, `clave filtrada`, `webhook filtrado`

**Siempre es SEV1** hasta demostrar lo contrario. Y antes que nada: **guardá evidencia**. Los logs en Elasticsearch son nuestro único registro de quién hizo qué.

**Caso A · Alguien entró a una cuenta que no es suya**

En PharmaGo las sesiones **no vencen**, no hay logout y cada login devuelve siempre el mismo token. Cambiar la clave **no alcanza**: el token robado sigue sirviendo. Hay que hacer las dos cosas, **en este orden**:

```bash
# 1. Cambiar la clave (si no, vuelve a entrar y obtiene un token nuevo)
pgsql "UPDATE Users SET Password='<clave-nueva>' WHERE UserName='<usuario>'"

# 2. Borrar la sesión (invalida el token robado al instante)
pgsql "DELETE FROM Sessions WHERE UserId=(SELECT Id FROM Users WHERE UserName='<usuario>')"
```

El corte es inmediato: los servicios validan el token contra la tabla `Sessions` en cada request. El dueño tiene que volver a entrar con la clave nueva.

Después, reconstruí qué hizo el intruso (Kibana, ajustando el rango de fechas):

```
Scopes.user_name : "<usuario>"
Scopes.pharma_biz : "purchase_create" and Scopes.user_name : "<usuario>"
```

Si creó o modificó datos → [PB-DATOS](#pb-datos).

**Caso B · Se filtró un secreto**

| Secreto | Qué hacer |
|---|---|
| Webhook de Discord | Borralo en Discord (Ajustes del canal → Integraciones), creá uno nuevo y cargalo con [PB-OTEL](#pb-otel), parte B |
| Clave `sa` de la BD | Está en el repo (`k8s/secrets/db-secret.yaml`) y es conocida. La BD no se expone fuera del cluster salvo por port-forward. Si el entorno pasa a ser accesible para terceros, hay que rotarla antes |
| Grafana `admin / admin` | Mismo criterio: cambiarla si Grafana queda expuesta |
| Token de sesión de un usuario | Caso A |

**Caso C · Intentos de inyección o payloads raros en los logs**

El escenario R3 verifica que la API contesta 4xx y nunca 5xx ante payloads maliciosos. Si ves 5xx asociados a entradas raras, tratalo como [PB-5XX](#pb-5xx) y guardá el `correlation_id`.

**No hagas:** borrar logs, usuarios o sesiones "para limpiar" antes de exportar la evidencia.

**Listo cuando:** el acceso está cortado, sabés qué se tocó y el dueño de la cuenta está avisado.

---

<a id="pb-nodo"></a>
### PB-NODO · No responde nada

**Buscar por:** `minikube`, `connection refused`, `Unable to connect to the server`, `000`, `todo caído`

**Qué significa:** todo corre en **un solo nodo** de Minikube. Si el nodo cae (o la máquina se reinició, o Docker Desktop se cerró), cae todo junto.

**Confirmar y contener:**

```bash
minikube status
minikube start                                  # levanta el nodo existente con sus datos
kubectl -n pharmago get pods -w                 # esperá a que todo vuelva (la BD y Elasticsearch tardan)
```

Después relanzá los accesos (`./port-forward.sh` o `minikube service pharmago-ui -n pharmago --url`): los túneles anteriores murieron.

Si los pods quedan `Pending` tras el arranque: `kubectl label nodes minikube node-type=all --overwrite`.

**No hagas:** `minikube delete`. Recrea el nodo desde cero y **se lleva la base de datos**, los logs y las métricas. Solo con decisión del IM y un backup hecho ([PB-DATOS](#pb-datos)).

**Listo cuando:** todos los deployments están completos, el pulso da 200 **y los datos siguen ahí** (entrá a la UI y verificá que aparezcan medicamentos y farmacias).

---

<a id="pb-caos"></a>
### PB-CAOS · Quedó un experimento a medias

**Buscar por:** `chaos`, `cleanup`, `netshoot`, `chaos_fill`, `chaos_lock`, `experimento`

**Qué significa:** se cortó un escenario (se cerró la terminal, se apagó la máquina) y pudo dejar réplicas en 0, reglas de red, procesos quemando CPU, un bloqueo en la BD o un tope en su archivo de datos.

```bash
cd chaos-engineering && ./chaos.sh cleanup
```

Es idempotente: se puede correr las veces que haga falta. Restaura réplicas, mata los procesos inyectados, quita reglas de red, libera la BD y borra los logs de la inundación.

**Para que esto no se confunda con un incidente real:**

- **Antes** de correr un experimento, avisá en el canal: `Arranco D3, unos 5 minutos`.
- Si llega una alerta y **nadie avisó** que estaba probando, se trata como real.

---

<a id="pb-datos"></a>
### PB-DATOS · Faltan datos o están dañados

**Buscar por:** `backup`, `restaurar`, `datos borrados`, `duplicado`, `corrupto`, `.bak`

**Es SEV1.** Y hay que decirlo claro: **hoy no tenemos backups automáticos**. Lo que haya para restaurar es lo que hayamos guardado a mano.

**Contener:**

1. **Sacá una copia del estado actual, antes de tocar nada.** Aunque esté dañado, es lo único que tenemos.
   ```bash
   F="PharmaDb-$(date +%Y%m%d-%H%M).bak"
   pgsql "BACKUP DATABASE PharmaDb TO DISK='/var/opt/mssql/data/$F' WITH INIT" master
   POD=$(kubectl -n pharmago get pod -l app=pharmago-db -o jsonpath='{.items[0].metadata.name}')
   kubectl -n pharmago cp "$POD:/var/opt/mssql/data/$F" "./$F"        # copia fuera del cluster
   ```
2. **¿Se sigue dañando?** Si un bug está escribiendo datos malos ahora, el IM decide si se frena el servicio que escribe: `kubectl -n pharmago scale deploy/<svc> --replicas=0`. Es una caída voluntaria; se justifica si cada minuto empeora el daño.
3. **Medí el alcance** antes de arreglar: qué tablas, desde cuándo, cuántas filas. Kibana ayuda con el cuándo (`Scopes.pharma_biz : "purchase_create"`).
4. **Restaurar** es decisión de los tres, no de uno. Fuentes posibles: un `.bak` propio anterior, o los datos de prueba de `Backend/seed-data.sql`.

**No hagas:** correr `UPDATE` o `DELETE` correctivos sin el backup del paso 1 y sin que otro mire la consulta.

**Listo cuando:** el daño dejó de crecer, hay una copia fuera del cluster y está acordado cómo se repara.

---

<a id="plantillas"></a>
## 9. Plantillas de mensajes

Copiar, completar y pegar en el hilo. Hechos y horas; nada de suposiciones presentadas como certezas.

<a id="plantilla-apertura"></a>
**Apertura**

```
🔴 INC-20261112-1 · SEV2 · ABIERTO
Qué pasa: /api/drug responde 503 desde las 14:32 (alerta "Circuit breaker abierto", target db:Drug)
Impacto: no se pueden ver ni comprar medicamentos. Login y farmacias funcionan.
Roles: IM <nombre> · Técnico <nombre> · Comunicación <nombre>
Próxima actualización: 14:50
```

**Actualización** (a la hora prometida, aunque sea para decir que no hay novedades)

```
INC-20261112-1 · SEV2 · 14:50
Sabemos: la BD responde; hay un bloqueo de esquema sobre la tabla Drugs.
Hicimos: evidencia guardada, sesión bloqueante identificada.
Sigue: matar esa sesión (aprobado por IM).
Próxima actualización: 15:05
```

**Bitácora** (una línea por hecho, la escribe Comunicación)

```
14:35 Técnico: pod de BD Running, SELECT 1 OK. Breaker abierto solo en db:Drug.
14:41 Técnico: sesión 58 tiene Sch-M sobre Drugs, program_name SQLCMD.
14:43 IM: aprobado KILL 58.
14:44 Técnico: ejecutado. /api/drug vuelve a 200 a las 14:44:20.
```

**Contenido**

```
🟡 INC-20261112-1 · CONTENIDO 14:44
El servicio volvió. Duración del impacto: 12 min.
Parche aplicado: se cortó la sesión que bloqueaba la tabla.
Pendiente: saber quién abrió esa transacción. Seguimos mirando 15 min.
```

**Cierre**

```
🟢 INC-20261112-1 · RESUELTO 15:02
Causa: un ALTER TABLE manual quedó con la transacción abierta.
Cambios de emergencia que quedaron aplicados: ninguno.
Postmortem: jueves 14/11, lo redacta <nombre>.
```

---

<a id="postmortem"></a>
## 10. Cerrar y aprender

<a id="cierre"></a>
### Antes de decir "resuelto"

- [ ] El [pulso](#pulso) dio bien tres veces seguidas, con un minuto entre cada una.
- [ ] `kubectl -n pharmago get deploy` muestra todo completo (apps 2/2).
- [ ] Llegó el `Resolved` de la alerta a Discord.
- [ ] Medición sostenida sin fallos: `./chaos-engineering/zero-downtime-test.sh "<URL de minikube service>" 120` (sin `--rollout` solo mide).
- [ ] Los cambios de emergencia están listados en el hilo, con una decisión para cada uno: **al repo** o **revertir**.
- [ ] La evidencia está guardada en un lugar que los tres pueden ver.
- [ ] Mensaje de cierre enviado, con fecha y responsable del postmortem.

### Postmortem

- **Cuándo:** siempre en SEV1 y SEV2, dentro de los 3 días hábiles. En SEV3 alcanza con tres líneas en el hilo.
- **Quién lo escribe:** el IM del incidente. Lo revisan los tres.
- **Dónde:** `incidentes/INC-AAAAMMDD-n.md`, al lado de este documento.
- **Sin culpables.** Se escribe qué pasó y por qué el sistema lo permitió, no quién se equivocó. "Fulano corrió un ALTER TABLE" no es una causa; "se puede correr DDL contra la BD sin revisión" sí.

```markdown
# INC-AAAAMMDD-n - <título en una línea>

- SEV: · Duración del impacto: · IM: · Técnico: · Comunicación:

## Resumen
Tres o cuatro frases que entienda alguien que no estuvo.

## Impacto
Qué no pudieron hacer los usuarios, cuántos, durante cuánto.

## Línea de tiempo
Copiada de la bitácora del hilo.

## Detección
¿Cómo nos enteramos? ¿Alerta o aviso de alguien? ¿Cuánto tardó?

## Causa
Preguntar "¿por qué?" hasta llegar a algo que se pueda cambiar (unas cinco veces).

## Qué salió bien / qué costó
Incluí lo que este plan no cubría o tenía mal.

## Acciones
| Acción | Responsable | Fecha |
|---|---|---|
```

**La regla de Atlassian que más rinde: nunca el mismo incidente dos veces.** Cada postmortem tiene que dejar al menos una de estas tres cosas:

1. Una alerta nueva o corregida.
2. Un escenario de caos que reproduzca lo que pasó.
3. Una corrección en este documento.

---

<a id="puntos-ciegos"></a>
## 11. Puntos ciegos que ya conocemos

Salen de los [hallazgos de la suite de caos](chaos-engineering/README.md). No están resueltos: hasta que lo estén, esto es lo que hacemos.

| Punto ciego | Consecuencia en un incidente | Mientras tanto |
|---|---|---|
| `/health` no chequea la BD | Con la BD caída los pods siguen `Ready`; Kubernetes no hace nada | Guiarse por la alerta del breaker y por el pulso |
| No hay alerta si cae el OTel Collector | Seis alertas quedan mudas sin aviso | [PB-OTEL](#pb-otel). Mirar los dashboards al empezar el día |
| La alerta de latencia no ve la red | Usuarios lentos, Grafana en verde | [PB-LATENCIA](#pb-latencia), parte B: medir desde afuera |
| La alerta de CPU tarda ~10 min | Una saturación de 6 minutos no avisa | Ante lentitud, mirar el panel de CPU aunque no haya alerta |
| El z-score de tráfico se tapa solo | Un segundo ataque en la misma hora no alerta | Tras una ráfaga, mirar el panel de 429 durante la hora siguiente |
| La BD es una sola instancia | Su caída tumba todas las rutas con datos | [PB-BD](#pb-bd). No hay redundancia posible con SQL Express |
| No hay backups automáticos | Una pérdida de datos puede no tener vuelta atrás | [PB-DATOS](#pb-datos). Hacer un backup manual antes de cada deploy con migración |
| Imágenes `:latest` | El rollback obliga a reconstruir: minutos en vez de segundos | [PB-DEPLOY](#pb-deploy). Mejora pendiente: etiquetar con el hash del commit |
| Sesiones eternas y claves en texto plano | Un token robado sirve para siempre | [PB-SEGURIDAD](#pb-seguridad), caso A |
| El rate limit vive en memoria por réplica | Un reinicio del gateway borra bloqueos; el límite real puede ser el doble | No reiniciar el gateway en medio de un ataque sin necesidad |
| Sin `ALERT_WEBHOOK_URL` las alertas no salen | Nadie se entera de nada | Comprobarlo después de cada despliegue ([PB-OTEL](#pb-otel), parte B) |
| El cluster vive en la máquina de una persona | Si no está, nadie puede ejecutar `kubectl` | Tabla de contactos al día. Idealmente, dos personas con el entorno listo |

---

<a id="preparacion"></a>
## 12. Antes de que pase

### Lista de preparación (revisarla los tres, una vez)

- [ ] Los tres tenemos las notificaciones del canal de alertas de Discord activas en el celular.
- [ ] La [tabla de contactos](#roles) tiene los teléfonos.
- [ ] El webhook está configurado y una alerta de prueba llegó ([PB-OTEL](#pb-otel), parte B).
- [ ] En Kibana existe el data view `pharmago-logs`.
- [ ] Cada uno corrió el [pulso](#pulso), el [bloque de evidencia](#evidencia) y la función [`pgsql`](#pgsql) al menos una vez, sin apuro.
- [ ] Acordamos en qué horarios esperamos respuesta y qué pasa fuera de ellos.

### Simulacros

Un plan que nunca se practicó falla la primera vez. La suite de caos ya trae los incidentes hechos: uno la corre **sin decir cuál**, los otros dos responden con este documento.

| Escenario | Dispara | Entrena |
|---|---|---|
| `./chaos.sh run D2` | Servicio sin réplicas disponibles | [PB-SERVICIO-CAIDO](#pb-servicio-caido) |
| `./chaos.sh run D3` o `N3` | Circuit breaker abierto (BD entera) | [PB-BD](#pb-bd), caso A |
| `./chaos.sh run C4` | Circuit breaker abierto (una tabla) + 5xx | [PB-BD](#pb-bd), caso B |
| `./chaos.sh run R2` | Logins fallidos anómalos | [PB-LOGIN](#pb-login) |
| `./chaos.sh run R1` | Tráfico anómalo | [PB-TRAFICO](#pb-trafico) |
| `./chaos.sh run C1` | CPU de pod cerca del límite | [PB-CPU](#pb-cpu) |
| `./chaos.sh run M1` | Memoria de pod cerca del límite | [PB-MEMORIA](#pb-memoria) |
| `./chaos.sh run D4` | Nada (ese es el punto) | [PB-OTEL](#pb-otel) |
| `./chaos.sh run N1` | Nada (ese es el punto) | [PB-LATENCIA](#pb-latencia), parte B |

Los escenarios se revierten solos al terminar. Usá las duraciones por defecto: con `--secs` más cortos las alertas no llegan a dispararse.

Después de cada simulacro, cinco minutos para una sola pregunta: **¿qué buscamos en este documento y no encontramos?** Eso se corrige acá el mismo día.

### Mantener este documento

- Cambia una alerta en `k8s/configmaps/grafana-alerting.yaml` → se actualiza el [índice](#indice) y su playbook.
- Se agrega un servicio o una ruta → se actualiza el [mapa de rutas](#pulso).
- Se cierra una deuda de la [sección 11](#puntos-ciegos) → se borra la fila y se ajusta el playbook.

---

### Glosario mínimo

| Término | Significado |
|---|---|
| **SEV** | Severidad del incidente (1 la más grave) |
| **IM** | Incident Manager: quien coordina y decide |
| **Contener** | Hacer que deje de doler, aunque sea con un parche |
| **MTTD / MTTR** | Tiempo hasta detectar / hasta recuperar |
| **Circuit breaker** | Corte automático: tras varias fallas seguidas deja de llamar a la BD un rato y responde 503 rápido |
| **Rate limit** | Tope de requests por usuario o IP; al pasarlo se responde 429 |
| **Postmortem** | Revisión escrita de qué pasó y qué cambiamos para que no se repita |
