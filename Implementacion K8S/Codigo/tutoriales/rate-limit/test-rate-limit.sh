#!/bin/bash

# Prueba manual del rate limiting (token bucket) del API Gateway.
# Uso: ./test-rate-limit.sh [URL] [TOKEN]
#
#   URL    por defecto http://127.0.0.1:5000/api/drug
#   TOKEN  (opcional) token de sesión de un usuario. Si se pasa, se hace además la prueba de aislamiento:
#          mientras la IP está agotada para anónimos, el usuario autenticado debe seguir recibiendo 200.
#
# Se envían muchas requests SEGUIDAS: el bucket anónimo permite una ráfaga de 100 y se recarga a ~1.7 tokens/s,
# así que con requests espaciadas (como las 120 a 0.1 s del límite anterior) ya no habría ningún bloqueo.

API_URL="${1:-http://127.0.0.1:5000/api/drug}"
TOKEN="$2"
TOTAL=250

echo "Test Rate Limiting (token bucket) - $TOTAL peticiones anónimas seguidas a $API_URL"
echo ""

success=0
rate_limited=0
retry_after=""

for i in $(seq 1 $TOTAL); do
    resp=$(curl -s -o /dev/null -D - -w "CODE:%{http_code}" "$API_URL" | tr -d '\r')
    code=$(echo "$resp" | sed -n 's/.*CODE:\([0-9]*\).*/\1/p')

    if [ "$code" = "200" ]; then
        success=$((success + 1))
    elif [ "$code" = "429" ]; then
        rate_limited=$((rate_limited + 1))
        [ -z "$retry_after" ] && retry_after=$(echo "$resp" | grep -i '^retry-after' | cut -d' ' -f2)
    else
        echo "[$i/$TOTAL] ✗ Error $code"
    fi
done

echo "Resultados:"
echo "  Exitosas (200):     $success   (esperado ~100 de ráfaga + lo que se recargue mientras corre)"
echo "  Rate Limited (429): $rate_limited"
[ -n "$retry_after" ] && echo "  Retry-After:        ${retry_after}s"
echo ""

if [ $rate_limited -gt 0 ]; then
    echo "✓ Rate limiting funciona"
else
    echo "⚠ Rate limiting no se activó"
fi

if [ -n "$TOKEN" ]; then
    echo ""
    echo "Aislamiento: la IP acaba de agotar su cubo anónimo. Ahora, desde la MISMA IP:"
    anon=$(curl -s -o /dev/null -w "%{http_code}" "$API_URL")
    auth=$(curl -s -o /dev/null -w "%{http_code}" -H "Authorization: $TOKEN" "$API_URL")
    echo "  anónimo:     HTTP $anon   (429 si el cubo sigue agotado; 200 si ya se recargó)"
    echo "  autenticado: HTTP $auth   (debe ser 200: tiene su propio cubo)"
    [ "$auth" = "200" ] && echo "✓ El usuario autenticado no se ve afectado" || echo "⚠ El usuario autenticado fue limitado"
fi
