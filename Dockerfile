# TesserChat server image (ARCHITECTURE.md 5.6).
#
# Build from the repository root, since the build stage needs Directory.Build.props and
# global.json as well as the projects themselves:
#
#   docker build -t tesserchat-server .
#
# The compose file in this directory does that for you, alongside a Postgres.

# ---------------------------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------------------------
# Pinned to a version rather than :latest, so an SDK release cannot change what this produces
# without a commit saying so. Matches global.json, which selects the SDK for the repository.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /source

# Copy the files that determine the restore graph first, so the restore layer is reused whenever
# only source files change. A restore is the slow part of this build.
COPY global.json Directory.Build.props ./
COPY src/TesserChat.Server/TesserChat.Server.csproj src/TesserChat.Server/
COPY src/TesserChat.Shared/TesserChat.Shared.csproj src/TesserChat.Shared/

# Only the server and what it references. The client is a desktop app and the test projects are
# not part of a deployment, so neither belongs in this image or its restore.
RUN dotnet restore src/TesserChat.Server/TesserChat.Server.csproj

COPY src/TesserChat.Server/ src/TesserChat.Server/
COPY src/TesserChat.Shared/ src/TesserChat.Shared/

RUN dotnet publish src/TesserChat.Server/TesserChat.Server.csproj \
    --configuration Release \
    --no-restore \
    --output /app

# ---------------------------------------------------------------------------------------------
# Runtime
# ---------------------------------------------------------------------------------------------
# aspnet rather than sdk: the runtime image is a fraction of the size and carries no compiler or
# package cache. Debian-slim rather than Alpine because NSec binds libsodium, whose native build
# targets glibc — Alpine's musl would need a different libsodium and buys little here.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# Npgsql probes for GSSAPI/Kerberos when opening a connection. The slim runtime image does not
# carry that library, which is harmless with password authentication but prints a "Cannot load
# library libgssapi_krb5.so.2" error on every boot that reads like a failure and is not.
# Installing it is cheaper than teaching every operator to ignore the message.
RUN apt-get update \
    && apt-get install --no-install-recommends -y libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*

# Runs as a non-root user, after the package install that needs root. The .NET images ship one for
# exactly this, so a container escape does not start as root, and nothing this server does needs
# privilege.
USER $APP_UID

WORKDIR /app
COPY --from=build /app .

# Plain HTTP inside the container. TLS belongs at the reverse proxy a self-hoster puts in front of
# this — terminating it here would mean managing certificates in two places and reloading the
# container to renew them.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# The liveness probe is the existing unauthenticated /health endpoint, which reports nothing about
# members, config, or identity. Written here rather than only in compose so that `docker run`
# reports health too.
#
# start-period covers first boot on a fresh database: migrations run before the server listens.
HEALTHCHECK --interval=30s --timeout=5s --start-period=40s --retries=3 \
    CMD ["dotnet", "TesserChat.Server.dll", "healthcheck"]

ENTRYPOINT ["dotnet", "TesserChat.Server.dll"]
