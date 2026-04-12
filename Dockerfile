FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

# Map Docker platform arch to .NET RID
RUN case "$TARGETARCH" in \
      amd64) echo "linux-x64" > /tmp/rid ;; \
      arm64) echo "linux-arm64" > /tmp/rid ;; \
      *) echo "linux-$TARGETARCH" > /tmp/rid ;; \
    esac

# Copy project files for restore layer caching
COPY Eidet.slnx ./
COPY src/Eidet.Core/Eidet.Core.csproj src/Eidet.Core/
COPY src/Eidet.Service/Eidet.Service.csproj src/Eidet.Service/
RUN dotnet restore src/Eidet.Service/Eidet.Service.csproj -r $(cat /tmp/rid)

# Copy source and publish
COPY src/ src/
RUN dotnet publish src/Eidet.Service/Eidet.Service.csproj \
    -c Release -r $(cat /tmp/rid) --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -o /app/publish

# Runtime image — use runtime-deps since self-contained
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS runtime
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish/Eidet.Service ./eidet

RUN mkdir -p /data/raven && chmod 755 /app/eidet

# Environment defaults for container operation
ENV DOTNET_RUNNING_IN_CONTAINER=true \
    EIDET_STORAGE_MODE=embedded \
    EIDET_DATA_DIR=/data/raven \
    EIDET_API_URL=http://0.0.0.0:19380 \
    EIDET_AUTH_REQUIRE_NONLOCALHOST=false

EXPOSE 19380
VOLUME /data

HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl -f http://localhost:19380/api/health || exit 1

ENTRYPOINT ["/app/eidet", "serve"]
