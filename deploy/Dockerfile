# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, against the manifests only, so a source-only change does not
# invalidate the restore layer.
# .editorconfig belongs here with the build props: it carries the analyser
# suppressions, and TreatWarningsAsErrors is on. Without it the image build fails
# on CA1716 and CA1711 for names the repository has deliberately kept — rules that
# pass everywhere else, which makes the image look like the broken thing.
COPY Directory.Build.props Directory.Packages.props NuGet.config global.json .editorconfig ./
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

# The base image already ships a non-root "app" user at uid 1654, and since .NET 8
# that is the intended way to drop privileges here. Creating one is not an option
# anyway: this base is Ubuntu chiselled and carries neither adduser nor useradd,
# so `RUN adduser` fails with exit 127 — which reads as a broken Dockerfile rather
# than a missing package.
#
# Running non-root does not contain the Docker socket, which is root-equivalent
# whoever holds it. The compose file adds the host's docker group instead of
# running as root, which narrows everything else the process can touch. See
# SECURITY.md for what socket access means.
COPY --from=build --chown=app:app /app .
USER app

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

# Never :latest for the API. The updater rewrites an explicit tag, and a floating
# tag would make "roll back to the previous version" meaningless.
LABEL org.opencontainers.image.source="https://github.com/TayoAderiye/airside"

ENTRYPOINT ["dotnet", "Airside.Api.dll"]
