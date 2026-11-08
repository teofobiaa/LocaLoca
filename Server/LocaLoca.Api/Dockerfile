# Build from the repository root:
# docker build -f Server/LocaLoca.Api/Dockerfile -t localoca-api .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json ./
COPY Server/LocaLoca.Api/LocaLoca.Api.csproj Server/LocaLoca.Api/
RUN dotnet restore Server/LocaLoca.Api/LocaLoca.Api.csproj

COPY . .
RUN dotnet publish Server/LocaLoca.Api/LocaLoca.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production
COPY --from=build /app/publish .
EXPOSE 10000
ENTRYPOINT ["dotnet", "LocaLoca.Api.dll"]
