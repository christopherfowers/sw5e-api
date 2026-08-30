# syntax=docker/dockerfile:1

# --- Build ---------------------------------------------------------------
#
# Pinned to the SDK feature band global.json asks for (10.0.302). The stage is
# pinned to the *builder's* architecture: the publish output below is portable
# IL with no apphost, so one build serves every target architecture and no
# emulation is needed to produce an arm64 image on an amd64 runner.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.302-alpine3.23 AS build

WORKDIR /src

# Restore against the project files alone first, so the slow restore layer is
# invalidated only when a dependency actually changes rather than on every
# source edit. Only the API's transitive project graph is copied; the test
# project is not part of the image and is excluded from the build context.
COPY global.json ./
COPY src/Sw5e.Api/Sw5e.Api.csproj src/Sw5e.Api/
COPY src/Sw5e.Domain/Sw5e.Domain.csproj src/Sw5e.Domain/
COPY src/Sw5e.Infrastructure/Sw5e.Infrastructure.csproj src/Sw5e.Infrastructure/
COPY src/Sw5e.Email/Sw5e.Email.csproj src/Sw5e.Email/
COPY src/Sw5e.Identity/Sw5e.Identity.csproj src/Sw5e.Identity/
COPY src/Sw5e.Migrator/Sw5e.Migrator.csproj src/Sw5e.Migrator/
RUN dotnet restore src/Sw5e.Api/Sw5e.Api.csproj \
 && dotnet restore src/Sw5e.Migrator/Sw5e.Migrator.csproj

COPY src/ src/

# Deliberately neither trimmed nor ReadyToRun.
#
# Trimming is unsafe here: minimal-API results and the content responses are
# serialised by System.Text.Json's reflection-based path — there is no
# JsonSerializerContext anywhere in the solution — so the trimmer would strip
# types that are only ever constructed by the serialiser and the failure would
# surface as an empty or throwing response at runtime, not at build time.
#
# ReadyToRun is skipped because it requires a RID-specific publish, which would
# force this stage to run once per target architecture under emulation for a
# startup saving that does not matter for a long-lived API container.
#
# UseAppHost=false drops the native launcher; the entrypoint runs the framework
# `dotnet` host from the runtime image instead, which keeps the output portable.
RUN dotnet publish src/Sw5e.Api/Sw5e.Api.csproj \
        --configuration Release \
        --no-restore \
        --output /app \
        -p:UseAppHost=false

# The deploy-time database job ships inside the API's image rather than in one
# of its own.
#
# One image makes it structurally impossible for the migrator and the API to be
# built from different commits, and that is the property that matters here: a
# schema and the code that reads it disagreeing is the exact failure this whole
# arrangement exists to prevent. Two images can be deployed at two versions; two
# entry points in one image cannot. It also halves what the release workflow
# builds and pushes and what a deploy has to pull.
#
# The migrator shares the API's dependency graph, so this adds a handful of
# small assemblies rather than a second runtime. Run it by overriding the entry
# point:
#
#   docker run --rm --entrypoint dotnet <image> Sw5e.Migrator.dll all
RUN dotnet publish src/Sw5e.Migrator/Sw5e.Migrator.csproj \
        --configuration Release \
        --no-restore \
        --output /app \
        -p:UseAppHost=false

# --- Runtime -------------------------------------------------------------
#
# Alpine rather than chiseled: chiseled images ship no shell and no HTTP
# client, so a HEALTHCHECK in one would either need an extra binary copied in
# or would have to be dropped. This image inherits BusyBox `wget` from Alpine,
# which makes the health probe below a real request rather than a no-op.
#
# The base image sets DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true (no ICU).
# That is safe for this app: every comparison, sort and case fold in the
# content index is ordinal or invariant.
#
# This stage contains no RUN instruction, so building it for a foreign
# architecture copies files and never executes anything.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.11-alpine3.23 AS runtime

# Kestrel binds 8080 only. Port 80 would require a privileged bind and the
# container does not run as root.
#
# Content:RootPath defaults to the read-only volume the compose stack mounts,
# populated by the sw5e-database init container. Override it with
# `Content__RootPath` if the volume is mounted elsewhere.
#
# EnableDiagnostics=0 turns off the diagnostic IPC socket, which the container
# has no use for and which would otherwise be created on every start.
ENV ASPNETCORE_URLS=http://+:8080 \
    Content__RootPath=/srv/content \
    DOTNET_EnableDiagnostics=0

WORKDIR /app
COPY --from=build /app ./

EXPOSE 8080

# APP_UID is defined by the base image as 1654, along with a matching `app`
# user and group. Nothing in the image is writable by that user, and the app
# never writes to disk.
USER $APP_UID

# BusyBox wget: -q silences it, -T bounds the read, and -O /dev/null discards
# the body. A non-2xx response or a refused connection exits non-zero, so an
# app that is up but failing is reported unhealthy rather than passing blindly.
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD wget -q -T 4 -O /dev/null http://127.0.0.1:8080/health || exit 1

ENTRYPOINT ["dotnet", "Sw5e.Api.dll"]
