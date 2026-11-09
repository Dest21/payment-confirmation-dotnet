# Imagen de la API (con la demo activable por configuración).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props .editorconfig ./
COPY src/Fudis.Payments/Fudis.Payments.csproj src/Fudis.Payments/
RUN dotnet restore src/Fudis.Payments/Fudis.Payments.csproj
COPY src/ src/
RUN dotnet publish src/Fudis.Payments -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Fudis.Payments.dll"]
