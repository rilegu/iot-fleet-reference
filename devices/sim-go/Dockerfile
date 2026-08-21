# Build stage. Pinned to the toolchain in go.mod so container and host builds match.
FROM golang:1.25-alpine AS build

WORKDIR /src

# Copy manifests first so dependency download is cached independently of source changes.
COPY go.mod go.sum ./
RUN go mod download

COPY . .

# Static binary: no cgo, so it runs on a scratch base with no libc.
# Trimpath and stripped symbols keep the image small; this is a simulator, not something
# anyone needs to attach a debugger to inside a container.
RUN CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /out/sim-go .

# Runtime stage.
FROM gcr.io/distroless/static-debian12:nonroot

COPY --from=build /out/sim-go /sim-go

# Non-root by default. The simulator needs no filesystem access and no privileged ports.
USER nonroot:nonroot

ENTRYPOINT ["/sim-go"]
