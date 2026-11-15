# Inicio Rápido

## Requisitos
- Docker y Docker Compose
- .NET 6.0 SDK (opcional, solo para desarrollo local sin Docker)

## Levantar la Aplicación Completa

### Con Docker Compose (Recomendado - Levanta TODO)

```bash
docker-compose up --build
```

Esto levanta automáticamente:
- Base de datos SQL Server
- Users Service
- Pharmacy Service  
- API Gateway
- Frontend Angular
- Prometheus
- Grafana
- OpenTelemetry Collector

### Acceso a Servicios

- **Frontend**: http://127.0.0.1:4200
- **API Gateway**: http://127.0.0.1:5000
- **Users Service**: http://127.0.0.1:5001
- **Pharmacy Service**: http://127.0.0.1:5002
- **Prometheus**: http://127.0.0.1:9090
- **Grafana**: http://127.0.0.1:3000 (admin/admin)
  - Datasource de Prometheus pre-configurado
  - Dashboard "PharmaGo - Overview" incluido

### Persistencia de Datos

Los datos se mantienen al reiniciar:

```bash
# Apagar servicios (datos se mantienen)
docker-compose down

# Volver a levantar (datos restaurados)
docker-compose up -d

# Apagar y eliminar TODOS los datos
docker-compose down -v
```

**Datos persistentes:**
- Base de datos SQL Server (usuario sa, contraseña Str0ngP@ssword!)
- Métricas de Prometheus (30 días)
- Dashboards de Grafana

---

## Desarrollo Local (Sin Docker - Opcional)

Solo si necesitas desarrollar/debuggear servicios individuales en tu IDE:

#### 1. Base de datos
```bash
docker-compose up pharmago-db
```

#### 2. Users Service
```bash
cd Backend/PharmaGo.UsersService
dotnet restore
dotnet run
```

#### 3. Pharmacy Service (nueva terminal)
```bash
cd Backend/PharmaGo.PharmacyService
dotnet restore
dotnet run
```

#### 4. API Gateway (nueva terminal)
```bash
cd Backend/PharmaGo.ApiGateway
dotnet restore
dotnet run
```

#### 5. Frontend (nueva terminal)
```bash
cd Frontend
npm install
npm start
```

## Detener Servicios

```bash
docker-compose down
```

## Ejecutar Tests

```bash
cd Backend/PharnaGo.Test
dotnet test
```

## Verificar Prometheus

```bash
# Acceder a Prometheus Targets
http://127.0.0.1:9090/targets

# Verificar métricas directamente
curl http://127.0.0.1:5000/metrics
curl http://127.0.0.1:5001/metrics
curl http://127.0.0.1:5002/metrics
```

