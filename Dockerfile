# Сервер компании: сборка и запуск
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /repo
COPY src/ src/
RUN dotnet publish src/EtsTracker.Server/EtsTracker.Server.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
RUN mkdir /data && chown app:app /data
ENV DATA_DIR=/data
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "EtsTracker.Server.dll"]
