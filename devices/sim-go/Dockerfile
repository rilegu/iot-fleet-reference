# Build context is the repository root: the simulator shares the module with the rest of
# the Go code, so the build needs go.mod from the root.
FROM golang:1.25-alpine AS build

WORKDIR /src

# Copy manifests first so dependency download is cached independently of source changes.
COPY go.mod go.sum ./
RUN go mod download

COPY contracts/ ./contracts/
# Shared instrumentation, used by every Go service.
COPY telemetry/ ./telemetry/
COPY devices/sim-go/ ./devices/sim-go/

# Static binary: no cgo, so it runs on a scratch base with no libc. Trimpath and stripped
# symbols keep the image small; nobody needs to attach a debugger inside this container.
RUN CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /out/sim-go ./devices/sim-go

FROM gcr.io/distroless/static-debian12:nonroot

COPY --from=build /out/sim-go /sim-go

# The simulator needs no filesystem access and no privileged ports.
USER nonroot:nonroot

ENTRYPOINT ["/sim-go"]
