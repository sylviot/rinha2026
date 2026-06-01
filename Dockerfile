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

FROM --platform=linux/amd64 debian:bookworm-slim AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates libstdc++6 zlib1g \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/FraudApi /app/FraudApi
# Pre-built dataset — eliminates the 60s preprocess on first boot.
COPY data/refs.bin /app/refs.bin
EXPOSE 8080
ENTRYPOINT ["/app/FraudApi"]
