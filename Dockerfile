# ─────────────────────────────────────────────────────────────
#  AgendaApi - imagen multi-etapa (compila desde el código)
#
#  Ya NO se requiere publicar localmente (antes se copiaba
#  publish_local/). `docker compose build` compila aquí mismo, lo
#  que permite desplegar en la nube directo desde el repo.
# ─────────────────────────────────────────────────────────────

# ─── Etapa build: compila con el SDK ─────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restaurar por proyecto (cachea dependencias)
COPY AgendaApi.Domain/AgendaApi.Domain.csproj    AgendaApi.Domain/
COPY AgendaApi.Application/AgendaApi.Application.csproj AgendaApi.Application/
COPY AgendaApi.Infrastructure/AgendaApi.Infrastructure.csproj AgendaApi.Infrastructure/
COPY AgendaApi.Api/AgendaApi.Api.csproj          AgendaApi.Api/
COPY AgendaApi.Tests/AgendaApi.Tests.csproj      AgendaApi.Tests/
COPY AgendaApi.sln ./
RUN dotnet restore AgendaApi.sln

# Compilar y publicar
COPY . .
RUN dotnet publish AgendaApi.Api/AgendaApi.Api.csproj -c Release -o /app/publish

# ─── Etapa final: runtime ligero ─────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "AgendaApi.Api.dll"]