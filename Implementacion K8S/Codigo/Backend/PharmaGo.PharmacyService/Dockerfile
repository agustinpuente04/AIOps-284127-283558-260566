FROM mcr.microsoft.com/dotnet/aspnet:6.0 AS base
WORKDIR /app
EXPOSE 80

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src

# Copy project files
COPY ["PharmaGo.PharmacyService/PharmaGo.PharmacyService.csproj", "PharmaGo.PharmacyService/"]
COPY ["PharmaGo.PharmacyService.BusinessLogic/PharmaGo.PharmacyService.BusinessLogic.csproj", "PharmaGo.PharmacyService.BusinessLogic/"]
COPY ["PharmaGo.PharmacyService.IBusinessLogic/PharmaGo.PharmacyService.IBusinessLogic.csproj", "PharmaGo.PharmacyService.IBusinessLogic/"]
COPY ["PharmaGo.PharmacyService.Factory/PharmaGo.PharmacyService.Factory.csproj", "PharmaGo.PharmacyService.Factory/"]
COPY ["PharmaGo.Domain/PharmaGo.Domain.csproj", "PharmaGo.Domain/"]
COPY ["PharmaGo.Exceptions/PharmaGo.Exceptions.csproj", "PharmaGo.Exceptions/"]
COPY ["PharmaGo.DataAccess/PharmaGo.DataAccess.csproj", "PharmaGo.DataAccess/"]
COPY ["PharmaGo.IDataAccess/PharmaGo.IDataAccess.csproj", "PharmaGo.IDataAccess/"]
COPY ["Instrumentation/Instrumentation.csproj", "Instrumentation/"]
COPY ["InstrumentationInterface/InstrumentationInterface.csproj", "InstrumentationInterface/"]
COPY ["ExportationModel/ExportationModel.csproj", "ExportationModel/"]
COPY ["JSONExporter/JSONExporter.csproj", "JSONExporter/"]

# Restore dependencies
RUN dotnet restore "PharmaGo.PharmacyService/PharmaGo.PharmacyService.csproj"

# Copy all source files
COPY . .

# Build (restore again to ensure all dependencies are resolved)
WORKDIR "/src/PharmaGo.PharmacyService"
RUN dotnet build "PharmaGo.PharmacyService.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "PharmaGo.PharmacyService.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "PharmaGo.PharmacyService.dll"]

