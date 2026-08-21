# Build context is the repository root: the dashboard references the shared client core,
# which lives outside its own directory.
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build

WORKDIR /src

# Restore against the project files alone so the layer caches independently of source edits.
COPY clients/dotnet/Fleet.Client.Core/Fleet.Client.Core.csproj ./clients/dotnet/Fleet.Client.Core/
COPY clients/dotnet/blazor/FleetDashboard.csproj ./clients/dotnet/blazor/
RUN dotnet restore clients/dotnet/blazor/FleetDashboard.csproj

COPY clients/dotnet/Fleet.Client.Core/ ./clients/dotnet/Fleet.Client.Core/
COPY clients/dotnet/blazor/ ./clients/dotnet/blazor/

# Note the absence of --no-restore.
#
# Publishing with --no-restore silently omits the Blazor framework static web assets:
# wwwroot/_framework is simply not emitted, so _framework/blazor.web.js returns 404 at
# runtime. The failure is invisible from the server side — the app starts cleanly, serves
# prerendered HTML, and the browser never creates a circuit, so the page just never becomes
# interactive and nothing is logged. The extra restore here costs a few seconds; the layer
# above still caches dependency download against the project files alone.
RUN dotnet publish clients/dotnet/blazor/FleetDashboard.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime

WORKDIR /app
COPY --from=build /out .

RUN adduser -D -u 10002 dashboard

# Create the key-ring directory in the image, owned by the app user. Docker initialises a
# named volume from the image's directory content and ownership, so mounting over a
# root-owned path here would leave the volume root-owned and the app unable to write to it.
RUN mkdir -p /keys && chown dashboard:dashboard /keys
VOLUME ["/keys"]

USER dashboard

EXPOSE 8090
ENV ASPNETCORE_URLS=http://+:8090

ENTRYPOINT ["dotnet", "FleetDashboard.dll"]
