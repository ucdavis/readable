FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /src

COPY ["workers/opendataloader.api/opendataloader.api.csproj", "workers/opendataloader.api/"]
RUN dotnet restore "workers/opendataloader.api/opendataloader.api.csproj"

COPY . .
RUN dotnet publish "workers/opendataloader.api/opendataloader.api.csproj" \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS final
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        openjdk-17-jre-headless \
        python3 \
        python3-pip \
    && rm -rf /var/lib/apt/lists/*

RUN python3 -m pip install --break-system-packages --no-cache-dir "opendataloader-pdf==2.3.0"

RUN useradd --create-home --uid 10001 appuser
WORKDIR /app
COPY --from=build /app/publish .
RUN chown -R appuser:appuser /app

ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080

USER appuser
ENTRYPOINT ["dotnet", "opendataloader.api.dll"]
