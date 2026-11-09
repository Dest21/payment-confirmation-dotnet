#!/usr/bin/env bash
# Corre las pruebas contra un SQL Server real, todo en Docker (no hace falta instalar .NET).
set -euo pipefail
cd "$(dirname "$0")/.."

NET=fudis-test-net
SQL=fudis-test-sql
PASS=Fudis_Local_123

docker network inspect "$NET" >/dev/null 2>&1 || docker network create "$NET" >/dev/null
if ! docker ps --format '{{.Names}}' | grep -qx "$SQL"; then
  docker rm -f "$SQL" >/dev/null 2>&1 || true
  docker run -d --name "$SQL" --network "$NET" -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD="$PASS" \
    mcr.microsoft.com/mssql/server:2022-latest >/dev/null
fi

echo "Esperando a SQL Server..."
for _ in $(seq 1 60); do
  docker exec "$SQL" /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$PASS" -Q "SELECT 1" >/dev/null 2>&1 && break
  sleep 2
done

docker run --rm --network "$NET" -v "$PWD":/src -w /src \
  -v fudis-nuget:/root/.nuget/packages \
  -e FUDIS_TEST_SQL="Server=$SQL,1433;User Id=sa;Password=$PASS;TrustServerCertificate=True" \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet test tests/Fudis.Payments.Tests --logger "console;verbosity=normal" --logger "trx;LogFileName=resultados.trx" \
  --results-directory /src/test-results "$@"
