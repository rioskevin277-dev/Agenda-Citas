FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

FROM base AS final
WORKDIR /app
COPY publish_local/ .
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "AgendaApi.Api.dll"]
