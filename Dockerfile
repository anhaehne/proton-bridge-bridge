FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
WORKDIR /source
COPY global.json Directory.Build.props ./
COPY src/ProtonBridgeProxy/*.csproj src/ProtonBridgeProxy/packages.lock.json src/ProtonBridgeProxy/
RUN dotnet restore src/ProtonBridgeProxy --locked-mode
COPY src/ProtonBridgeProxy/ src/ProtonBridgeProxy/
RUN dotnet publish src/ProtonBridgeProxy -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13-noble@sha256:8af87eba5e7cdafb1514d7388be287365da97c132509e84dfe252ffd552398ca
WORKDIR /app
COPY --from=build /out ./
COPY LICENSE /app/LICENSE
USER 1654:1654
HEALTHCHECK --interval=30s --timeout=10s --start-period=10s --retries=3 CMD ["dotnet", "ProtonBridgeProxy.dll", "--healthcheck", "/config/config.json"]
ENTRYPOINT ["dotnet", "ProtonBridgeProxy.dll"]
CMD ["--experimental", "/config/config.json"]
