#!/bin/bash
# S2 - Sobrecarga de storage en la BD: agota el espacio del archivo de datos de SQL Server (error 1105)
# SIN llenar el disco del nodo. Se fija MAXSIZE = tamaño actual del archivo (no puede crecer) y se rellena
# una tabla temporal hasta que ya no queda espacio.
# Rollback: DROP TABLE chaos_fill + MAXSIZE UNLIMITED (también lo hace `chaos.sh cleanup`).
# Uso: ./s2-bd-sin-espacio.sh [--secs 120] [--url URL]
source "$(dirname "${BASH_SOURCE[0]}")/../lib/common.sh"
scenario_init S2 "BD sin espacio (storage de SQL Server agotado)" 120 "$@"

hypothesis "Con el archivo de datos lleno las escrituras fallan (error 1105) pero las lecturas siguen respondiendo 200 y los pods no se reinician."

steady_state
restarts_snapshot
sql "SELECT 1" master >/dev/null 2>&1 || die "No se pudo ejecutar sqlcmd en $DB."

LNAME="$(sql "SET NOCOUNT ON; SELECT TOP 1 name FROM sys.database_files WHERE type=0" "$DB_NAME" | tr -d '\r' | grep -v '^$' | head -1)"
SIZE_MB="$(sql "SET NOCOUNT ON; SELECT TOP 1 CAST(CEILING(size*8.0/1024) AS int) FROM sys.database_files WHERE type=0" "$DB_NAME" | tr -d '\r ' | grep -E '^[0-9]+$' | head -1)"
GROWTH="$(sql "SET NOCOUNT ON; SELECT TOP 1 CASE WHEN is_percent_growth=1 THEN CONCAT(growth,'%') ELSE CONCAT(growth*8/1024,'MB') END FROM sys.database_files WHERE type=0" "$DB_NAME" | tr -d '\r ' | grep -E '^[0-9]+(MB|%)$' | head -1)"
[ -n "$LNAME" ] && [ -n "$SIZE_MB" ] && [ -n "$GROWTH" ] || die "No se pudo leer el archivo de datos de $DB_NAME."
PARAMS+=("base de datos: $DB_NAME, archivo lógico: $LNAME, tamaño: ${SIZE_MB}MB, FILEGROWTH original: $GROWTH (durante la prueba: MAXSIZE=${SIZE_MB}MB, FILEGROWTH=1MB)")

db_restore() {
  sql "IF OBJECT_ID('dbo.chaos_fill') IS NOT NULL DROP TABLE dbo.chaos_fill" "$DB_NAME" >/dev/null
  # SQL Server solo admite cambiar UNA propiedad por MODIFY FILE (y valida MAXSIZE contra el FILEGROWTH vigente)
  sql "ALTER DATABASE [$DB_NAME] MODIFY FILE (NAME = N'$LNAME', MAXSIZE = UNLIMITED)" master >/dev/null
  sql "ALTER DATABASE [$DB_NAME] MODIFY FILE (NAME = N'$LNAME', FILEGROWTH = $GROWTH)" master >/dev/null
}
on_cleanup db_restore

probe_start
phase antes; hold "$PRE_SECS"

phase durante
sql "ALTER DATABASE [$DB_NAME] MODIFY FILE (NAME = N'$LNAME', FILEGROWTH = 1MB)" master >/dev/null || die "No se pudo fijar FILEGROWTH."
sql "ALTER DATABASE [$DB_NAME] MODIFY FILE (NAME = N'$LNAME', MAXSIZE = ${SIZE_MB}MB)" master >/dev/null || die "No se pudo fijar MAXSIZE."
sql "IF OBJECT_ID('dbo.chaos_fill') IS NULL CREATE TABLE dbo.chaos_fill (id int identity, pad char(8000) NOT NULL DEFAULT 'x')" "$DB_NAME" >/dev/null
log "Llenando el archivo de datos hasta el error 1105..."
FILL_OUT="$(sql "SET NOCOUNT ON; WHILE 1=1 INSERT dbo.chaos_fill DEFAULT VALUES" "$DB_NAME")"
echo "$FILL_OUT" | grep -qE '1105|Could not allocate space' && FULL=0 || FULL=1
ROWS="$(sql "SET NOCOUNT ON; SELECT COUNT(*) FROM dbo.chaos_fill" "$DB_NAME" | tr -d '\r ' | grep -E '^[0-9]+$' | head -1)"
[ "$FULL" = 0 ] || { echo "$FILL_OUT" | tail -5 >&2; die "El archivo no llegó a llenarse (error 1105 no observado)."; }
log "Archivo lleno tras ${ROWS:-?} filas de 8 KB."
PARAMS+=("filas insertadas hasta el error 1105: ${ROWS:-?} (~$(( ${ROWS:-0} * 8 / 1024 ))MB)")

# Verificaciones directas contra la BD
sql "INSERT dbo.chaos_fill DEFAULT VALUES" "$DB_NAME" | grep -qE '1105|Could not allocate space' && W=0 || W=1
assert_true "Una escritura en la BD falla con error 1105 (sin espacio)" "$W"
hold "$SECS"

log "Rollback: DROP TABLE + MAXSIZE UNLIMITED"
db_restore
phase despues; hold "$POST_SECS"
probe_stop

assert_no_5xx durante "$DB_PATHS"
assert_avail durante 99
assert_avail despues 100
restarts_delta "pharmago" >/dev/null
assert_eq "Pods reiniciados con la BD sin espacio" "$RESTART_DELTA_TOTAL" 0
LEFT="$(sql "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.tables WHERE name='chaos_fill'" "$DB_NAME" | tr -d '\r ' | grep -E '^[0-9]+$' | head -1)"
assert_eq "La tabla de relleno fue eliminada (rollback)" "${LEFT:-?}" 0
note "Este escenario no ejercita una escritura de la aplicación (requiere un token de usuario). Cubre el comportamiento de la BD y de las lecturas con el archivo de datos agotado."
finish
