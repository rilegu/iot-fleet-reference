# Build context is the repository root: this binary embeds the schemas from contracts/, so
# the build needs more than its own directory.
FROM golang:1.25-alpine AS build

WORKDIR /src

COPY go.mod go.sum ./
RUN go mod download

COPY contracts/ ./contracts/
# Shared instrumentation, used by every Go service.
COPY telemetry/ ./telemetry/
COPY services/ingest/ ./services/ingest/

RUN CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /out/ingest ./services/ingest

FROM gcr.io/distroless/static-debian12:nonroot

COPY --from=build /out/ingest /ingest

USER nonroot:nonroot
EXPOSE 9101

ENTRYPOINT ["/ingest"]
