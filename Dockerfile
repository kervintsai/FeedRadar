FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["FeedRadarWebServer/FeedRadarWebServer.csproj", "FeedRadarWebServer/"]
RUN dotnet restore "FeedRadarWebServer/FeedRadarWebServer.csproj"
COPY FeedRadarWebServer/ FeedRadarWebServer/
RUN dotnet publish "FeedRadarWebServer/FeedRadarWebServer.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
COPY feeds.db .
ENTRYPOINT ["dotnet", "FeedRadarWebServer.dll"]
