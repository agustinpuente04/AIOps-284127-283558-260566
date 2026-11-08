# Test Rate Limiting

Script simple para probar el rate limiting (token bucket) del API Gateway.
El diseño completo y el porqué de cada decisión están en [`Backend/RATE_LIMITING_GUIDE.md`](../../Backend/RATE_LIMITING_GUIDE.md).

## Uso

```bash
cd tutoriales/rate-limit
chmod +x test-rate-limit.sh
./test-rate-limit.sh                         # prueba anónima
./test-rate-limit.sh http://127.0.0.1:5000/api/drug <TOKEN>   # + prueba de aislamiento con un usuario autenticado
```

Para obtener un token: `curl -s -X POST http://127.0.0.1:5000/api/login -H 'Content-Type: application/json' -d '{"userName":"admin","password":"Abcdef12."}'`

## Qué hace

- Envía 250 peticiones GET **seguidas** a `/api/drug` sin token.
- Muestra cuántas fueron exitosas (200), cuántas fueron bloqueadas (429) y el `Retry-After` del primer 429.
- Si se pasa un token, comprueba además que un usuario autenticado de la misma IP sigue recibiendo 200.

## Resultado esperado

El cubo anónimo permite una **ráfaga de 100** y se recarga a **~1.7 tokens/s** (100 por minuto):

- ~100 peticiones exitosas + las que se recarguen mientras corre el script (con un `curl` por petición, unas decenas más).
- El resto, bloqueadas con **429**, con cabecera `Retry-After` de pocos segundos.
- Con token: el usuario autenticado recibe **200** aunque la IP esté agotada para anónimos.
- Si se espera unos segundos y se repite, vuelven a pasar peticiones: el bucket **se recupera de forma gradual**; no hay un bloqueo de una hora como en el límite anterior.

> Con varias réplicas del gateway cada una tiene su propio cubo, así que en el cluster pasan hasta ~100 por réplica.
> Un `port-forward` apunta a una sola réplica.

## Requisitos

- API Gateway corriendo en `http://127.0.0.1:5000` (o la URL que se pase como primer argumento)
- Modo `TokenBucket` (el valor por defecto de `RateLimiting:Mode`)
- Para la prueba de aislamiento, un token de sesión válido (lo valida users-service)

Para pruebas de carga más completas (ráfagas, fuerza bruta de login, aislamiento) ver los escenarios `R1`, `R2` y `R4` de [`chaos-engineering/`](../../chaos-engineering/README.md).
