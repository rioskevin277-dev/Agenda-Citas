FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY *.sln .
COPY AgendaApi.Domain/*.csproj AgendaApi.Domain/
COPY AgendaApi.Application/*.csproj AgendaApi.Application/
COPY AgendaApi.Infrastructure/*.csproj AgendaApi.Infrastructure/
COPY AgendaApi.Api/*.csproj AgendaApi.Api/
RUN dotnet restore

COPY . .
RUN dotnet publish AgendaApi.Api/AgendaApi.Api.csproj \
    -c Release \
    -o /app/publish \
    --self-contained false \
    --no-restore

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "AgendaApi.Api.dll"]
