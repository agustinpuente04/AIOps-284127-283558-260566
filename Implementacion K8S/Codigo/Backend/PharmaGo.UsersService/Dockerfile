FROM mcr.microsoft.com/dotnet/aspnet:6.0 AS base
WORKDIR /app
EXPOSE 80

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src

# Copy project files
COPY ["PharmaGo.UsersService/PharmaGo.UsersService.csproj", "PharmaGo.UsersService/"]
COPY ["PharmaGo.UsersService.BusinessLogic/PharmaGo.UsersService.BusinessLogic.csproj", "PharmaGo.UsersService.BusinessLogic/"]
COPY ["PharmaGo.UsersService.IBusinessLogic/PharmaGo.UsersService.IBusinessLogic.csproj", "PharmaGo.UsersService.IBusinessLogic/"]
COPY ["PharmaGo.UsersService.Factory/PharmaGo.UsersService.Factory.csproj", "PharmaGo.UsersService.Factory/"]
COPY ["PharmaGo.Domain/PharmaGo.Domain.csproj", "PharmaGo.Domain/"]
COPY ["PharmaGo.Exceptions/PharmaGo.Exceptions.csproj", "PharmaGo.Exceptions/"]
COPY ["PharmaGo.DataAccess/PharmaGo.DataAccess.csproj", "PharmaGo.DataAccess/"]
COPY ["PharmaGo.IDataAccess/PharmaGo.IDataAccess.csproj", "PharmaGo.IDataAccess/"]
COPY ["Instrumentation/Instrumentation.csproj", "Instrumentation/"]
COPY ["InstrumentationInterface/InstrumentationInterface.csproj", "InstrumentationInterface/"]

# Restore dependencies
RUN dotnet restore "PharmaGo.UsersService/PharmaGo.UsersService.csproj"

# Copy all source files
COPY . .

# Build (restore again to ensure all dependencies are resolved)
WORKDIR "/src/PharmaGo.UsersService"
RUN dotnet build "PharmaGo.UsersService.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "PharmaGo.UsersService.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "PharmaGo.UsersService.dll"]

