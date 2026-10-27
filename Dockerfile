FROM mcr.microsoft.com/dotnet/sdk:9.0.315 AS build
WORKDIR /source
COPY global.json Directory.Build.props ./
COPY src/CmsEventService/CmsEventService.csproj src/CmsEventService/
RUN dotnet restore src/CmsEventService/CmsEventService.csproj
COPY src/ src/
RUN dotnet publish src/CmsEventService/CmsEventService.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "CmsEventService.dll"]
