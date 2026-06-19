# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Yatch.fsproj src/
RUN dotnet restore src/Yatch.fsproj
COPY src/ src/
RUN dotnet publish src/Yatch.fsproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
RUN mkdir -p /data
ENV DB_PATH=/data/yatch.db
ENV HOST=0.0.0.0
ENV PORT=5000
# Aliyun OSS / S3-compatible stores reject the AWS SDK's default integrity
# checksums — only send them when explicitly required.
ENV AWS_REQUEST_CHECKSUM_CALCULATION=when_required
ENV AWS_RESPONSE_CHECKSUM_VALIDATION=when_required
EXPOSE 5000
ENTRYPOINT ["dotnet", "yatch.dll"]
