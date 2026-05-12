#!/bin/sh
set -eu

default_run_migrations=false
if [ "${ASPNETCORE_ENVIRONMENT:-}" = "Production" ] || [ "${ASPNETCORE_ENVIRONMENT:-}" = "Staging" ]; then
  default_run_migrations=true
fi

run_migrations="${Database__RunMigrationsBeforeApiStartup:-${DATABASE_RUN_MIGRATIONS_BEFORE_API_STARTUP:-$default_run_migrations}}"

if [ "$run_migrations" = "true" ] || [ "$run_migrations" = "1" ]; then
  echo "[ENTRYPOINT] Running Esportra.Migrator before API startup..."
  dotnet /app/migrator/Esportra.Migrator.dll
  echo "[ENTRYPOINT] Esportra.Migrator completed successfully."
fi

exec dotnet /app/Esportra.Api.dll
