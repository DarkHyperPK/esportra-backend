FROM mcr.microsoft.com/dotnet/runtime:9.0 AS base
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY ["src/Esportra.Migrator/Esportra.Migrator.csproj", "src/Esportra.Migrator/"]
COPY ["src/Esportra.Infrastructure/Esportra.Infrastructure.csproj", "src/Esportra.Infrastructure/"]
COPY ["src/Esportra.Core/Esportra.Core.csproj", "src/Esportra.Core/"]
COPY ["src/Esportra.Contracts/Esportra.Contracts.csproj", "src/Esportra.Contracts/"]

RUN dotnet restore "src/Esportra.Migrator/Esportra.Migrator.csproj"

COPY . .
RUN dotnet publish "src/Esportra.Migrator/Esportra.Migrator.csproj" -c Release -o /app/publish --no-restore

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Esportra.Migrator.dll"]
