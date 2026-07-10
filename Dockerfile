FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Labby.csproj .
RUN dotnet restore Labby.csproj
COPY . .
RUN dotnet publish Labby.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ARG LABBY_VERSION=dev
ENV LABBY_VERSION=$LABBY_VERSION
# librespeed-cli powers the optional scheduled speed tests (Network:SpeedtestHours).
ADD https://github.com/librespeed/speedtest-cli/releases/download/v1.0.11/librespeed-cli_1.0.11_linux_amd64.tar.gz /tmp/librespeed.tar.gz
RUN tar xzf /tmp/librespeed.tar.gz -C /usr/local/bin librespeed-cli && rm /tmp/librespeed.tar.gz
COPY --from=build /app/publish .
EXPOSE 8080
# The aspnet image ships no curl/wget, so probe /healthz with bash's /dev/tcp instead.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /healthz HTTP/1.1\\r\\nHost: localhost\\r\\nConnection: close\\r\\n\\r\\n' >&3 && grep -q '200 OK' <&3"]
ENTRYPOINT ["dotnet", "Labby.dll"]
