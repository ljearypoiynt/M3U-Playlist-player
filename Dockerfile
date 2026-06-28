FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY M3UPlaylistPlayer.Gateway/M3UPlaylistPlayer.Gateway.csproj M3UPlaylistPlayer.Gateway/
RUN dotnet restore M3UPlaylistPlayer.Gateway/M3UPlaylistPlayer.Gateway.csproj

COPY . .
RUN dotnet publish M3UPlaylistPlayer.Gateway/M3UPlaylistPlayer.Gateway.csproj \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates ffmpeg \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production \
    Gateway__Urls=http://0.0.0.0:5055 \
    HOME=/data
EXPOSE 5055

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "M3UPlaylistPlayer.Gateway.dll"]
