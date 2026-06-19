# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Yatch.fsproj src/
RUN dotnet restore src/Yatch.fsproj
COPY src/ src/
# Framework-dependent publish (the chiseled aspnet base supplies the runtime).
# Trimming/self-contained is deliberately avoided: AWS SDK + Npgsql + FSharp.Core
# are reflection-heavy and not trim/AOT-safe — see the build notes.
RUN dotnet publish src/Yatch.fsproj -c Release -o /app --no-restore
# Pre-create an empty data dir to COPY into the shell-less chiseled stage.
RUN mkdir -p /seed-data

# Chiseled = distroless (no shell/pkg manager), runs as non-root (UID 1654),
# no ICU (we set InvariantGlobalization). Far smaller + smaller attack surface
# than the full Debian aspnet base.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
WORKDIR /app
COPY --from=build /app ./
# SQLite fallback path; in production DATABASE_URL points at Postgres so this is
# unused, but keep it writable by the non-root user just in case.
COPY --from=build --chown=$APP_UID:$APP_UID /seed-data /data
ENV DB_PATH=/data/yatch.db
ENV HOST=0.0.0.0
ENV PORT=5000
# Aliyun OSS / S3-compatible stores reject the AWS SDK's default integrity
# checksums — only send them when explicitly required.
ENV AWS_REQUEST_CHECKSUM_CALCULATION=when_required
ENV AWS_RESPONSE_CHECKSUM_VALIDATION=when_required
EXPOSE 5000
ENTRYPOINT ["dotnet", "yatch.dll"]
