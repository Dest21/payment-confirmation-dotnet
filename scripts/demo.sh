#!/usr/bin/env bash
# Levanta SQL Server y la API en modo demo (Development). Abre http://localhost:5080
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

docker rm -f fudis-demo >/dev/null 2>&1 || true
echo "Demo en http://localhost:5080 (Ctrl+C para detener)"
docker run --rm --name fudis-demo --network "$NET" -p 127.0.0.1:5080:8080 \
  -v "$PWD":/src -w /src -v fudis-nuget:/root/.nuget/packages \
  -e ASPNETCORE_ENVIRONMENT=Development \
  -e ConnectionStrings__Fudis="Server=$SQL,1433;Database=FudisDemo;User Id=sa;Password=$PASS;TrustServerCertificate=True" \
  -e Demo__Enabled=true -e Demo__SelfUrl=http://localhost:8080 \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet run --project src/Fudis.Payments --urls http://0.0.0.0:8080
