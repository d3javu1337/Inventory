FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /source/backend

COPY backend/global.json backend/Directory.Build.props backend/Directory.Packages.props backend/NuGet.Config backend/AssetInventory.sln ./
COPY backend/src ./src

RUN dotnet restore src/AssetInventory.Api/AssetInventory.Api.csproj --configfile NuGet.Config --disable-parallel
RUN dotnet publish src/AssetInventory.Api/AssetInventory.Api.csproj --no-restore -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12 AS runtime
WORKDIR /app
RUN mkdir -p /app/storage && chown -R $APP_UID /app
USER $APP_UID
COPY --from=build --chown=$APP_UID /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "AssetInventory.Api.dll"]
