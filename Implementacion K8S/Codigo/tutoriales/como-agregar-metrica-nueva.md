# Guía: Cómo Agregar una Nueva Métrica Custom

Esta guía te muestra cómo agregar una nueva métrica personalizada al sistema de monitoreo de PharmaGo.

## 📋 Pasos para Agregar una Nueva Métrica

### Paso 1: Agregar el Método en la Interfaz

Edita `Backend/InstrumentationInterface/ICustomMetrics.cs` y agrega la firma del método:

```csharp
public interface ICustomMetrics
{
    // ... métodos existentes ...
    
    /// <summary>
    /// Registra una compra de medicamento
    /// </summary>
    void RecordDrugPurchase(string drugId, string pharmacyId, double amount);
}
```

### Paso 2: Implementar el Método en CustomMetrics

Edita `Backend/Instrumentation/CustomMetrics.cs`:

#### 2.1. Agregar el campo privado para el instrumento

```csharp
public class CustomMetrics : ICustomMetrics, IDisposable
{
    private readonly Meter _meter;
    // ... campos existentes ...
    private readonly Counter<long> _drugPurchasesCounter; // ← NUEVO
    
    public CustomMetrics()
    {
        _meter = new Meter("PharmaGo.CustomMetrics");
        
        // ... instrumentos existentes ...
        
        // Crear el nuevo contador
        _drugPurchasesCounter = _meter.CreateCounter<long>(
            "pharmago_drug_purchases_total",
            unit: "1",
            description: "Total number of drug purchases by drug and pharmacy"
        );
    }
}
```

#### 2.2. Implementar el método público

```csharp
public void RecordDrugPurchase(string drugId, string pharmacyId, double amount)
{
    var tags = new KeyValuePair<string, object?>[]
    {
        new("drug_id", drugId),
        new("pharmacy_id", pharmacyId),
        new("amount", amount)
    };
    _drugPurchasesCounter.Add(1, tags);
}
```

### Paso 3: Usar la Métrica en tu Código

Inyecta `ICustomMetrics` en tu controlador o servicio y úsala:

```csharp
using InstrumentationInterface;
using Microsoft.AspNetCore.Mvc;

namespace PharmaGo.PharmacyService.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PurchaseController : ControllerBase
    {
        private readonly ICustomMetrics _metrics; // ← Inyectar
        
        public PurchaseController(ICustomMetrics metrics)
        {
            _metrics = metrics;
        }
        
        [HttpPost]
        public IActionResult CreatePurchase([FromBody] PurchaseRequest request)
        {
            // Tu lógica de negocio aquí
            var purchase = CreatePurchaseInternal(request);
            
            // Registrar la métrica
            _metrics.RecordDrugPurchase(
                purchase.DrugId, 
                purchase.PharmacyId, 
                purchase.Amount
            );
            
            return Ok(purchase);
        }
    }
}
```

### Paso 4: Verificar que el Meter esté Registrado

El meter `"PharmaGo.CustomMetrics"` ya está registrado en OpenTelemetry en todos los servicios. Verifica en:

- `Backend/PharmaGo.ApiGateway/Program.cs` (línea 43)
- `Backend/PharmaGo.UsersService.Factory/TelemetryExtensions.cs` (línea 18)
- `Backend/PharmaGo.PharmacyService.Factory/TelemetryExtensions.cs` (línea 18)

Todos tienen: `.AddMeter("PharmaGo.CustomMetrics")`

## 📊 Tipos de Métricas Disponibles

### 1. Counter (Contador)
**Uso:** Para contar eventos que solo aumentan (peticiones, compras, errores)

```csharp
private readonly Counter<long> _myCounter;

_myCounter = _meter.CreateCounter<long>(
    "my_counter_total",
    unit: "1",
    description: "Description of what this counter measures"
);

// Usar:
_myCounter.Add(1, tags); // Incrementar en 1
_myCounter.Add(5, tags); // Incrementar en 5
```

### 2. Histogram (Histograma)
**Uso:** Para medir distribuciones de valores (latencia, tamaño, duración)

```csharp
private readonly Histogram<double> _myHistogram;

_myHistogram = _meter.CreateHistogram<double>(
    "my_histogram",
    unit: "ms",
    description: "Description of what this histogram measures"
);

// Usar:
_myHistogram.Record(123.45, tags); // Registrar un valor
```

### 3. Gauge (Medidor)
**Uso:** Para valores que suben y bajan (usuarios activos, memoria, temperatura)

#### Gauge Simple (ObservableGauge)
```csharp
private int _activeConnections;

_meter.CreateObservableGauge(
    "active_connections",
    () => new Measurement<int>[] { new Measurement<int>(_activeConnections) },
    unit: "1",
    description: "Current number of active connections"
);

// Actualizar el valor:
_activeConnections = 10; // El gauge se actualiza automáticamente
```

## 🏷️ Labels/Tags

Los labels permiten segmentar las métricas. Ejemplos:

```csharp
// Labels simples
var tags = new KeyValuePair<string, object?>[]
{
    new("endpoint", "/api/drug"),
    new("method", "GET"),
    new("status_code", "200")
};

// Labels con valores numéricos
var tags = new KeyValuePair<string, object?>[]
{
    new("drug_id", drugId),
    new("pharmacy_id", pharmacyId),
    new("amount", amount) // ← Puede ser double, int, etc.
};
```

## 📈 Consultar la Métrica en Prometheus

Una vez agregada, la métrica estará disponible en Prometheus:

```promql
# Ver todas las compras
pharmago_drug_purchases_total

# Compras por farmacia
sum by (pharmacy_id) (rate(pharmago_drug_purchases_total[5m]))

# Compras por medicamento
sum by (drug_id) (rate(pharmago_drug_purchases_total[5m]))

# Total de compras en la última hora
sum(increase(pharmago_drug_purchases_total[1h]))
```

## 🔍 Verificar que Funciona

1. **Verificar en `/metrics`:**
   ```bash
   curl http://localhost:5000/metrics | grep pharmago_drug_purchases_total
   ```

2. **Verificar en Prometheus:**
   - Ir a http://localhost:9090
   - Buscar: `pharmago_drug_purchases_total`

3. **Agregar a Grafana:**
   - Crear un nuevo panel en el dashboard
   - Usar la query: `sum(rate(pharmago_drug_purchases_total[5m]))`

## ⚠️ Mejores Prácticas

1. **Nombres de métricas:**
   - Usar snake_case: `pharmago_drug_purchases_total`
   - Agregar `_total` para counters: `pharmago_http_requests_total`
   - Ser descriptivo pero conciso

2. **Labels:**
   - No usar demasiados labels (máximo 5-10)
   - Evitar labels con alta cardinalidad (como IDs únicos)
   - Usar labels para segmentación útil

3. **Unidades:**
   - Especificar siempre la unidad: `"ms"`, `"bytes"`, `"1"` (sin unidad)
   - Ser consistente con las unidades existentes

4. **Descripciones:**
   - Escribir descripciones claras y útiles
   - Explicar qué mide la métrica y cuándo se incrementa

## 📝 Ejemplo Completo: Métrica de Compras

### 1. Interfaz (`ICustomMetrics.cs`)
```csharp
/// <summary>
/// Registra una compra de medicamento
/// </summary>
void RecordDrugPurchase(string drugId, string pharmacyId, double amount);
```

### 2. Implementación (`CustomMetrics.cs`)
```csharp
private readonly Counter<long> _drugPurchasesCounter;

public CustomMetrics()
{
    _meter = new Meter("PharmaGo.CustomMetrics");
    
    _drugPurchasesCounter = _meter.CreateCounter<long>(
        "pharmago_drug_purchases_total",
        unit: "1",
        description: "Total number of drug purchases by drug and pharmacy"
    );
}

public void RecordDrugPurchase(string drugId, string pharmacyId, double amount)
{
    var tags = new KeyValuePair<string, object?>[]
    {
        new("drug_id", drugId),
        new("pharmacy_id", pharmacyId),
        new("amount", amount)
    };
    _drugPurchasesCounter.Add(1, tags);
}
```

### 3. Uso en Controlador
```csharp
[HttpPost]
public IActionResult CreatePurchase([FromBody] PurchaseRequest request)
{
    var purchase = CreatePurchaseInternal(request);
    
    _metrics.RecordDrugPurchase(
        purchase.DrugId, 
        purchase.PharmacyId, 
        purchase.Amount
    );
    
    return Ok(purchase);
}
```

### 4. Query en Prometheus
```promql
# Total de compras por segundo
sum(rate(pharmago_drug_purchases_total[5m]))

# Compras por farmacia
sum by (pharmacy_id) (rate(pharmago_drug_purchases_total[5m]))

# Top 10 medicamentos más comprados
topk(10, sum by (drug_id) (rate(pharmago_drug_purchases_total[5m])))
```

## ✅ Checklist

- [ ] Método agregado en `ICustomMetrics.cs`
- [ ] Campo privado del instrumento agregado en `CustomMetrics.cs`
- [ ] Instrumento creado en el constructor de `CustomMetrics`
- [ ] Método implementado en `CustomMetrics.cs`
- [ ] Métrica usada en el código donde corresponde
- [ ] Verificado en `/metrics` endpoint
- [ ] Verificado en Prometheus
- [ ] (Opcional) Agregado panel en Grafana

