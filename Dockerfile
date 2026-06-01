# syntax=docker/dockerfile:1.7
FROM --platform=linux/amd64 mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

RUN apt-get update && apt-get install -y --no-install-recommends \
    clang zlib1g-dev \
 && rm -rf /var/lib/apt/lists/*

COPY src/FraudApi/FraudApi.csproj ./FraudApi/
RUN dotnet restore ./FraudApi/FraudApi.csproj -r linux-x64

COPY src/FraudApi/. ./FraudApi/
RUN dotnet publish ./FraudApi/FraudApi.csproj -c Release -r linux-x64 \
    --no-restore -o /app /p:PublishAot=true /p:StripSymbols=true

# Preprocess stage: generate refs.bin at build time so runtime startup is instant.
FROM --platform=linux/amd64 debian:bookworm-slim AS preprocess
RUN apt-get update && apt-get install -y --no-install-recommends \
    libstdc++6 zlib1g \
 && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/FraudApi /app/FraudApi
COPY data/references.json.gz /input/references.json.gz
COPY data/mcc_risk.json /input/mcc_risk.json
RUN mkdir -p /refs && PREPROCESS_ONLY=1 /app/FraudApi

FROM --platform=linux/amd64 debian:bookworm-slim AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates libstdc++6 zlib1g \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/FraudApi /app/FraudApi
COPY --from=preprocess /refs/refs.bin /app/refs.bin
EXPOSE 8080
ENTRYPOINT ["/app/FraudApi"]
