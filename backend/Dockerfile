# Contesto di build: root del repository (vedi infra/compose.yaml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY backend/src/bProjects.App/bProjects.App.csproj backend/src/bProjects.App/packages.lock.json backend/src/bProjects.App/
RUN dotnet restore backend/src/bProjects.App/bProjects.App.csproj --locked-mode
COPY backend/src/bProjects.App backend/src/bProjects.App
RUN dotnet publish backend/src/bProjects.App/bProjects.App.csproj -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /out .
# Radice privata degli originali (Documents__StorageRoot): il volume nominato eredita proprietario e permessi di questa cartella.
RUN mkdir -p /var/lib/bprojects/documents && chown $APP_UID:$APP_UID /var/lib/bprojects/documents && chmod 700 /var/lib/bprojects/documents
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "bProjects.App.dll"]
