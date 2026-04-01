FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY ["src/Esportra.Api/Esportra.Api.csproj", "src/Esportra.Api/"]
COPY ["src/Esportra.Core/Esportra.Core.csproj", "src/Esportra.Core/"]
COPY ["src/Esportra.Infrastructure/Esportra.Infrastructure.csproj", "src/Esportra.Infrastructure/"]
COPY ["src/Esportra.Contracts/Esportra.Contracts.csproj", "src/Esportra.Contracts/"]

RUN dotnet restore "src/Esportra.Api/Esportra.Api.csproj"

COPY . .
RUN dotnet build "src/Esportra.Api/Esportra.Api.csproj" -c Release

FROM build AS publish
RUN dotnet publish "src/Esportra.Api/Esportra.Api.csproj" -c Release -o /app/publish --no-build

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Esportra.Api.dll"]
