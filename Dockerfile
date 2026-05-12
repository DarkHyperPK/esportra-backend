FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY ["src/Esportra.Api/Esportra.Api.csproj", "src/Esportra.Api/"]
COPY ["src/Esportra.Core/Esportra.Core.csproj", "src/Esportra.Core/"]
COPY ["src/Esportra.Infrastructure/Esportra.Infrastructure.csproj", "src/Esportra.Infrastructure/"]
COPY ["src/Esportra.Contracts/Esportra.Contracts.csproj", "src/Esportra.Contracts/"]
COPY ["src/Esportra.Migrator/Esportra.Migrator.csproj", "src/Esportra.Migrator/"]

RUN dotnet restore "src/Esportra.Api/Esportra.Api.csproj"
RUN dotnet restore "src/Esportra.Migrator/Esportra.Migrator.csproj"

COPY . .
RUN dotnet build "src/Esportra.Api/Esportra.Api.csproj" -c Release

FROM build AS publish
RUN dotnet publish "src/Esportra.Api/Esportra.Api.csproj" -c Release -o /app/publish --no-build
RUN dotnet publish "src/Esportra.Migrator/Esportra.Migrator.csproj" -c Release -o /app/migrator --no-restore

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
COPY --from=publish /app/migrator ./migrator
COPY ["deploy/api-entrypoint.sh", "/app/api-entrypoint.sh"]
RUN chmod +x /app/api-entrypoint.sh
ENTRYPOINT ["/app/api-entrypoint.sh"]
