# Contexto de build: raiz do repositório (regras de build e versões centrais).
# Imagens fixadas por tag exata e digest: uma tag pode ser movida (ADR-0055).
FROM mcr.microsoft.com/dotnet/sdk:10.0.302@sha256:72dd743782f2ae7e5476fd64f6a460045e3998dc862218b80e6944cba79a01b0 AS build
WORKDIR /repo
COPY . .
# A auditoria NuGet barra pacote vulnerável no CI e no build local; na imagem, um alerta novo não impede o compose de subir (ADR-0055).
RUN dotnet publish src/Hosts/Api/Api.csproj -c Release -o /app -p:NuGetAudit=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.10@sha256:f1126d438ccc359f51cc6d4701a8deae513856cf10f5fe645d29ea6403dcac6b
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "Api.dll"]
