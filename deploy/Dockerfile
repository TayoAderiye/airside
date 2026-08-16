# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, against the manifests only, so a source-only change does not
# invalidate the restore layer.
COPY Directory.Build.props Directory.Packages.props NuGet.config global.json ./
COPY src/Airside.Core/Airside.Core.csproj                                   src/Airside.Core/
COPY src/Airside.Data/Airside.Data.csproj                                   src/Airside.Data/
COPY src/Airside.Data.Migrations.Postgres/Airside.Data.Migrations.Postgres.csproj src/Airside.Data.Migrations.Postgres/
COPY src/Airside.Data.Migrations.Sqlite/Airside.Data.Migrations.Sqlite.csproj     src/Airside.Data.Migrations.Sqlite/
COPY src/Airside.Runtime/Airside.Runtime.csproj                             src/Airside.Runtime/
COPY src/Airside.Api/Airside.Api.csproj                                     src/Airside.Api/
RUN dotnet restore src/Airside.Api/Airside.Api.csproj

COPY src/ src/
RUN dotnet publish src/Airside.Api/Airside.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# The image declares a non-root user, but the Docker socket is root-owned on most
# hosts. The compose file adds the host's docker group id instead of running as
# root — see docker-compose.yml, and SECURITY.md for what socket access means.
RUN adduser --system --uid 10001 --group airside
COPY --from=build /app .
RUN chown -R airside:airside /app
USER airside

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

# Never :latest for the API. The updater rewrites an explicit tag, and a floating
# tag would make "roll back to the previous version" meaningless.
LABEL org.opencontainers.image.source="https://github.com/tayoriye/airside"

ENTRYPOINT ["dotnet", "Airside.Api.dll"]
