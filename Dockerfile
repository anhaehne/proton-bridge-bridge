FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
WORKDIR /source
COPY global.json Directory.Build.props ./
COPY src/ProtonBridgeProxy/*.csproj src/ProtonBridgeProxy/packages.lock.json src/ProtonBridgeProxy/
RUN dotnet restore src/ProtonBridgeProxy --locked-mode
COPY src/ProtonBridgeProxy/ src/ProtonBridgeProxy/
RUN dotnet publish src/ProtonBridgeProxy -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0.13-noble@sha256:0cf09aaccf6a9f68900aed4c73fb28fe25bb59ec85de4dea08ba02240a8635f3
WORKDIR /app
COPY --from=build /out ./
COPY LICENSE /app/LICENSE
USER 1654:1654
ENTRYPOINT ["dotnet", "ProtonBridgeProxy.dll"]
CMD ["--experimental", "/config/config.json"]
