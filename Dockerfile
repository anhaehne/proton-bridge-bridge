FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
ARG SOURCE_REVISION=unknown
WORKDIR /source
COPY global.json Directory.Build.props ./
COPY src/ProtonBridgeBridge/*.csproj src/ProtonBridgeBridge/packages.lock.json src/ProtonBridgeBridge/
RUN dotnet restore src/ProtonBridgeBridge --locked-mode
COPY src/ProtonBridgeBridge/ src/ProtonBridgeBridge/
RUN dotnet publish src/ProtonBridgeBridge -c Release --no-restore -o /out /p:UseAppHost=false /p:SourceRevisionId="$SOURCE_REVISION"

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13-noble@sha256:8af87eba5e7cdafb1514d7388be287365da97c132509e84dfe252ffd552398ca
ARG SOURCE_REVISION=unknown
LABEL org.opencontainers.image.title="ProtonBridgeBridge" \
      org.opencontainers.image.version="0.1.0-rc.1" \
      org.opencontainers.image.revision="$SOURCE_REVISION" \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app
COPY --from=build /out ./
COPY LICENSE THIRD-PARTY-NOTICES.txt /app/
USER 1654:1654
HEALTHCHECK --interval=30s --timeout=10s --start-period=10s --retries=3 CMD ["dotnet", "ProtonBridgeBridge.dll", "--healthcheck", "/config/config.json"]
ENTRYPOINT ["dotnet", "ProtonBridgeBridge.dll"]
CMD ["--experimental", "/config/config.json"]
