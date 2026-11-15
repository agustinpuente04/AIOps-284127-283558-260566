# Cómo Agregar Log Estructurado

## Servicio de Logging Estructurado

El proyecto incluye `IStructuredLogger` que encapsula la lógica de logging estructurado. Solo necesitas inyectarlo y usarlo.

### Ejemplo: Log de Login

El login ya tiene logging estructurado implementado usando `IStructuredLogger`.

### Implementación

En `LoginController.cs`:

```csharp
using InstrumentationInterface;

public class LoginController : ControllerBase
{
    private readonly IStructuredLogger _structuredLogger;

    public LoginController(ILoginManager manager, IStructuredLogger structuredLogger)
    {
        _structuredLogger = structuredLogger;
    }

    [HttpPost]
    public IActionResult Login([FromBody] LoginModelRequest userModel)
    {
        try
        {
            var authorization = _loginManager.Login(userModel.UserName, userModel.Password);
            
            // Log exitoso
            _structuredLogger.LogInformation(
                $"User {authorization.UserName} logged in successfully",
                new Dictionary<string, object>
                {
                    ["status"] = "success",
                    ["message"] = $"User {authorization.UserName} logged in successfully",
                    ["user_name"] = authorization.UserName
                }
            );
            
            return Ok(authorization);
        }
        catch (Exception ex)
        {
            // Log fallido
            _structuredLogger.LogWarning(
                $"User {userModel.UserName} failed log in",
                ex,
                new Dictionary<string, object>
                {
                    ["status"] = "failed",
                    ["message"] = $"User {userModel.UserName} failed log in",
                    ["user_name"] = userModel.UserName ?? "unknown"
                }
            );
            
            throw;
        }
    }
}
```

### Metadata Automática

`IStructuredLogger` agrega automáticamente:
- `log_level`: "Information", "Warning" o "Error"
- `timestamp`: Fecha/hora UTC

Solo necesitas pasar metadata adicional como `status`, `message`, `user_name`, etc.

### Métodos Disponibles

```csharp
// Log de información
_structuredLogger.LogInformation(message, metadata);

// Log de advertencia (con o sin excepción)
_structuredLogger.LogWarning(message, exception, metadata);

// Log de error (con o sin excepción)
_structuredLogger.LogError(message, exception, metadata);
```

## Ver Logs en Kibana

### 1. Acceder a Kibana
```
http://localhost:5601
```

### 2. Ir a Discover
- Selecciona el data view `pharmago-logs-*`

### 3. Filtrar Logs de Login

#### Ver todos los logs de login:
```
message: "logged in" OR message: "failed log in"
```

#### Ver solo logins exitosos:
```
message: "logged in successfully" AND status: "success"
```

#### Ver solo logins fallidos:
```
message: "failed log in" AND status: "failed"
```

#### Filtrar por usuario específico:
```
user_name: "nombre_usuario"
```

#### Combinar filtros:
```
message: "logged in" AND status: "success" AND log_level: "Information"
```

### 4. Query Language de Kibana

**Operadores básicos:**
- `AND`: Y lógico
- `OR`: O lógico
- `NOT`: Negación
- `*`: Comodín

**Ejemplos:**

```
# Logs de un usuario específico
user_name: "admin"

# Logs de login exitosos
status: "success" AND log_level: "Information"

# Logs de login fallidos
status: "failed" AND log_level: "Warning"

# Buscar por parte del mensaje
message: *successfully*

# Excluir logs de un usuario
NOT user_name: "test"
```

### 5. Visualizar Campos

En Discover, puedes agregar columnas para ver:
- `message`
- `status`
- `log_level`
- `user_name`
- `timestamp`

## Agregar Log Estructurado a Otros Endpoints

1. Inyectar `IStructuredLogger` en el constructor
2. Llamar al método correspondiente con metadata

```csharp
_structuredLogger.LogInformation(
    "Operación completada exitosamente",
    new Dictionary<string, object>
    {
        ["status"] = "success",
        ["message"] = "Descripción de la operación",
        ["campo_personalizado"] = valor
    }
);
```

**Ventajas:**
- ✅ Lógica centralizada
- ✅ Metadata automática (log_level, timestamp)
- ✅ Fácil de usar
- ✅ Consistente en todo el proyecto

