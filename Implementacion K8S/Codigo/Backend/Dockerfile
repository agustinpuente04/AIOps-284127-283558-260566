FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR .

COPY ["PharmaGo.WebApi/PharmaGo.WebApi.csproj", "PharmaGo.WebApi/"]
COPY ["PharmaGo.Factory/PharmaGo.Factory.csproj", "PharmaGo.Factory/"]
COPY ["PharmaGo.DataAccess/PharmaGo.DataAccess.csproj", "PharmaGo.DataAccess/"]
RUN dotnet restore "PharmaGo.WebApi/PharmaGo.WebApi.csproj"

COPY . .
RUN dotnet publish "PharmaGo.WebApi/PharmaGo.WebApi.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:6.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 80

ENV ASPNETCORE_URLS=http://+:80
ENV ASPNETCORE_ENVIRONMENT=Development

ENTRYPOINT ["dotnet", "PharmaGo.WebApi.dll"]
