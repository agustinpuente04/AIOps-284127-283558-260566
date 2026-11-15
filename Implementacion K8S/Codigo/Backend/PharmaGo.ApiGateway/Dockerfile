FROM mcr.microsoft.com/dotnet/aspnet:6.0 AS base
WORKDIR /app
EXPOSE 80

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src

# Copy project files
COPY ["PharmaGo.ApiGateway/PharmaGo.ApiGateway.csproj", "PharmaGo.ApiGateway/"]
COPY ["Instrumentation/Instrumentation.csproj", "Instrumentation/"]
COPY ["InstrumentationInterface/InstrumentationInterface.csproj", "InstrumentationInterface/"]

# Restore dependencies
RUN dotnet restore "PharmaGo.ApiGateway/PharmaGo.ApiGateway.csproj"

# Copy all source files
COPY . .

# Build (restore again to ensure all dependencies are resolved)
WORKDIR "/src/PharmaGo.ApiGateway"
RUN dotnet build "PharmaGo.ApiGateway.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "PharmaGo.ApiGateway.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "PharmaGo.ApiGateway.dll"]

