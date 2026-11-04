FROM mcr.microsoft.com/dotnet/sdk:10.0-azurelinux3.0@sha256:ffd76bb46785445d2d029809009d35b522619b97df88c0abaa05d549db697b43 AS dotnet
FROM node:22-bookworm@sha256:18a6d90fd60542269850c80ca03b29b1d9b14d6b221ac7ee8e35b0d33ef904bd

COPY --from=dotnet /usr/share/dotnet /usr/share/dotnet
ENV DOTNET_ROOT=/usr/share/dotnet \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    PATH="/usr/share/dotnet:${PATH}"

USER node
