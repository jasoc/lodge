# Read-only checks that run under Lodge's restrictive container profile: nothing in the
# action relaxes it (no `security:` block), so the container has no capabilities, no new
# privileges, a read-only root filesystem and a non-root user. Nothing here needs more.
# The base image is pinned by digest: a rebuild produces the image that was approved.
FROM alpine:3.21@sha256:ce64758a109eb420d874a118f87920e625e12d3634e03b4a5573fd9f6e5d3507

# curl + jq: the verification talks to Lodge's own API to invalidate what it finds stale.
RUN apk add --no-cache ca-certificates curl jq

COPY --chmod=0755 run.sh /usr/local/bin/verify
USER 10001:10001
ENTRYPOINT ["/usr/local/bin/verify"]
