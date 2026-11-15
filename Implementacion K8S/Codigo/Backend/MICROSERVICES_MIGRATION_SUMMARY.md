# Microservices Migration Summary

## Overview
El backend monolítico de PharmaGo ha sido migrado exitosamente a una arquitectura de microservicios con dos servicios principales y un API Gateway.

## Arquitectura

### Servicios Implementados

1. **Users Service** (Puerto 5001)
   - Responsabilidades: Gestión de usuarios, roles, invitaciones, autenticación
   - Entidades: User, Role, Invitation, Session, Pharmacy (réplica)
   - Controllers: LoginController, UsersController, InvitationsController, RolesController

2. **Pharmacy Operations Service** (Puerto 5002)
   - Responsabilidades: Gestión de farmacias, medicamentos, compras, stock
   - Entidades: Drug, Purchase, PurchaseDetail, StockRequest, UnitMeasure, Presentation, Pharmacy
   - Controllers: DrugController, PharmacyController, PurchasesController, StockRequestController, UnitMeasuresController, PresentationsController, ExportController

3. **API Gateway** (Puerto 5000)
   - Tecnología: YARP (Yet Another Reverse Proxy)
   - Función: Enrutamiento centralizado de peticiones a los microservicios
   - El frontend sigue apuntando al puerto 5000, sin cambios necesarios

## Estructura de Proyectos

### Proyectos Compartidos
- `PharmaGo.Domain` - Entidades del dominio
- `PharmaGo.Exceptions` - Excepciones personalizadas
- `PharmaGo.IDataAccess` - Interfaces de acceso a datos
- `PharmaGo.DataAccess` - Implementación de acceso a datos (EF Core)
- `Instrumentation` / `InstrumentationInterface` - Métricas y telemetría
- `ExportationModel` / `JSONExporter` - Exportación de datos

### Proyectos del Users Service
- `PharmaGo.UsersService` - WebAPI
- `PharmaGo.UsersService.BusinessLogic` - Lógica de negocio
- `PharmaGo.UsersService.IBusinessLogic` - Interfaces de lógica de negocio
- `PharmaGo.UsersService.Factory` - Configuración de DI y telemetría

### Proyectos del Pharmacy Service
- `PharmaGo.PharmacyService` - WebAPI
- `PharmaGo.PharmacyService.BusinessLogic` - Lógica de negocio
- `PharmaGo.PharmacyService.IBusinessLogic` - Interfaces de lógica de negocio
- `PharmaGo.PharmacyService.Factory` - Configuración de DI y telemetría

### API Gateway
- `PharmaGo.ApiGateway` - Gateway con YARP

## Base de Datos

### Estrategia: Base de Datos Compartida
- Ambos microservicios comparten la misma base de datos SQL Server
- Ventajas:
  - No requiere transacciones distribuidas
  - Migraciones centralizadas
  - Consistencia de datos garantizada
- Migraciones: Ejecutadas desde Users Service

## Comunicación entre Servicios

### HTTP/REST
- **Users Service → Pharmacy Service**: PharmacyServiceClient
- **Pharmacy Service → Users Service**: UsersServiceClient
- Configuración con `IHttpClientFactory` y políticas de retry
- Timeouts configurados a 30 segundos

### Configuración
```json
{
  "ServiceUrls": {
    "PharmacyService": "http://pharmago-pharmacy-service:80",
    "UsersService": "http://pharmago-users-service:80"
  }
}
```

## Docker Compose

### Servicios Configurados
- `pharmago-db`: SQL Server 2019
- `pharmago-users-service`: Users microservice
- `pharmago-pharmacy-service`: Pharmacy microservice
- `pharmago-api-gateway`: API Gateway
- `pharmago-ui`: Frontend Angular
- `otlp-collector`: OpenTelemetry Collector
- `prometheus`: Métricas
- `grafana`: Visualización

### Red
- Red Docker: `pharmago-network` (bridge)
- Comunicación interna entre servicios

## Observabilidad

### OpenTelemetry
- Métricas configuradas en todos los servicios (Users, Pharmacy, API Gateway)
- Doble exportación:
  - **Prometheus Exporter**: Endpoint `/metrics` en cada servicio
  - **OTLP Exporter**: Envío a OTLP Collector (puerto 4317)
- Service names:
  - `PharmaGo.UsersService`
  - `PharmaGo.PharmacyService`
  - `PharmaGo.ApiGateway`

### Prometheus & Grafana
- **Prometheus**: http://localhost:9090
  - Scraping configurado para los 3 servicios + OTLP Collector
  - Intervalo de scraping: 5 segundos
  - Targets: `/metrics` en cada servicio
- **Grafana**: http://localhost:3000 (admin/admin)
  - Data source: Prometheus
  - Listo para crear dashboards personalizados

### Métricas Disponibles
- **ASP.NET Core**: Requests HTTP, latencia, errores
- **HTTP Client**: Llamadas entre servicios
- **Custom Metrics**: LoginInvocations y otras métricas de negocio
- Ver `PROMETHEUS_CONFIGURATION.md` para detalles completos

## Pruebas Unitarias

### Tests Existentes Mantenidos
- Los tests unitarios existentes siguen funcionando
- La base de datos compartida permite que los tests accedan a todas las entidades
- Tests de BusinessLogic, DataAccess y Controllers están organizados por servicio

### Estructura de Tests
- Tests de Users Service: LoginManager, UsersManager, InvitationManager, RoleManager
- Tests de Pharmacy Service: DrugManager, PharmacyManager, PurchasesManager, StockManager, etc.
- Tests de DataAccess: Compartidos, funcionan con ambos servicios

## Cómo Ejecutar

### Desarrollo Local
```bash
# Users Service
cd Backend/PharmaGo.UsersService
dotnet run

# Pharmacy Service
cd Backend/PharmaGo.PharmacyService
dotnet run

# API Gateway
cd Backend/PharmaGo.ApiGateway
dotnet run
```

### Docker Compose
```bash
docker-compose up --build
```

### Acceso a Servicios
- API Gateway: http://localhost:5000
- Users Service (directo): http://localhost:5001
- Pharmacy Service (directo): http://localhost:5002
- Frontend: http://localhost:4200
- Swagger Users: http://localhost:5001/swagger
- Swagger Pharmacy: http://localhost:5002/swagger
- Swagger Gateway: http://localhost:5000/swagger

## Rutas del API Gateway

### Users Service
- `/api/users/**` → UsersService
- `/api/roles/**` → UsersService
- `/api/invitations/**` → UsersService
- `/api/login/**` → UsersService

### Pharmacy Service
- `/api/drug/**` → PharmacyService
- `/api/pharmacy/**` → PharmacyService
- `/api/purchases/**` → PharmacyService
- `/api/stockrequest/**` → PharmacyService
- `/api/unitmeasure/**` → PharmacyService
- `/api/presentation/**` → PharmacyService
- `/api/export/**` → PharmacyService

## Consideraciones de Seguridad

- CORS configurado en todos los servicios
- Autenticación mediante tokens (compartida entre servicios vía DB)
- AuthorizationFilter funciona en ambos servicios
- Comunicación interna entre servicios sin autenticación (red privada Docker)

## Próximos Pasos Recomendados

1. **Separar Base de Datos**: Considerar migrar a bases de datos independientes por servicio
2. **Event-Driven Architecture**: Implementar mensajería asíncrona (RabbitMQ, Kafka)
3. **Service Mesh**: Considerar Istio o Linkerd para gestión avanzada de tráfico
4. **Circuit Breakers**: Implementar Polly para resiliencia
5. **API Versioning**: Implementar versionado de APIs
6. **Health Checks**: Agregar endpoints de health check
7. **Distributed Tracing**: Mejorar trazabilidad con Jaeger o Zipkin
8. **Tests de Integración**: Crear tests específicos para comunicación entre servicios

## Notas Importantes

- El frontend **NO requiere cambios** ya que sigue apuntando al puerto 5000 (API Gateway)
- La entidad Pharmacy está presente en ambos servicios:
  - Pharmacy Service: Dueño de la entidad (CRUD completo)
  - Users Service: Réplica de solo lectura (vía HTTP cuando sea necesario)
- Las migraciones de base de datos se ejecutan desde Users Service al iniciar
- Los tests unitarios existentes se mantienen sin cambios significativos

## Conclusión

La migración a microservicios se ha completado exitosamente, manteniendo toda la funcionalidad existente mientras se mejora la escalabilidad, mantenibilidad y separación de responsabilidades del sistema.

