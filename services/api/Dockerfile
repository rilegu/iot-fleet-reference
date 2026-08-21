# Build context is the repository root, matching the other services.
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build

WORKDIR /src

# Restore against the project file alone so the layer caches independently of source edits.
COPY services/api/FleetApi.csproj ./services/api/
RUN dotnet restore services/api/FleetApi.csproj

COPY services/api/ ./services/api/

RUN dotnet publish services/api/FleetApi.csproj \
    -c Release \
    -o /out \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime

WORKDIR /app
COPY --from=build /out .

# Non-root. The API needs no filesystem access beyond its own binaries.
RUN adduser -D -u 10001 fleet
USER fleet

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "FleetApi.dll"]
