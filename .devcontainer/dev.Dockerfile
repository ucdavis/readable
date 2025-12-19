FROM mcr.microsoft.com/devcontainers/base:ubuntu

# Base utilities only (removed 'sqlcmd' – it was invalid)
RUN apt-get update && apt-get install -y --no-install-recommends \
    curl jq bash-completion procps iputils-ping netcat-traditional \
    && rm -rf /var/lib/apt/lists/*

# NodeJS 22
RUN curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
    && apt-get update \
    && apt-get install -y --no-install-recommends nodejs \
    && rm -rf /var/lib/apt/lists/*

# .NET 8.0 SDK (version must match global.json)
RUN wget https://dot.net/v1/dotnet-install.sh -O /tmp/dotnet-install.sh \
    && chmod +x /tmp/dotnet-install.sh \
    && /tmp/dotnet-install.sh --version 8.0.400 --install-dir /usr/share/dotnet \
    && ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet \
    && rm /tmp/dotnet-install.sh

# Azure Functions Core Tools v4
RUN curl -fsSL https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -o /tmp/packages-microsoft-prod.deb \
    && dpkg -i /tmp/packages-microsoft-prod.deb \
    && rm /tmp/packages-microsoft-prod.deb \
    && apt-get update \
    && apt-get install -y --no-install-recommends azure-functions-core-tools-4 \
    && rm -rf /var/lib/apt/lists/*

# base image already has user 'vscode' with good perms
USER vscode
WORKDIR /workspace
