#!/usr/bin/env bash
# Pull the published image and redeploy the stack.
#
# This machine is a consumer only - nothing is built here. deploy.sh on the
# dev box builds, tags and pushes; this script just brings the result down.
set -euo pipefail

cd "$(dirname "$0")"

for f in .env deploy.env; do
  if [[ -f "$f" ]]; then
    set -a
    # shellcheck disable=SC1090
    source "$f"
    set +a
  fi
done

: "${SUPABASE_URL:?set SUPABASE_URL}"
: "${SUPABASE_KEY:?set SUPABASE_KEY}"

IMAGE_NAME="${IMAGE_NAME:-liorgr/phone-orchestrator}"
IMAGE_TAG="${IMAGE_TAG:-latest}"
STACK="${STACK:-orchestrator}"
IMAGE="${IMAGE_NAME}:${IMAGE_TAG}"

echo "==> Pulling $IMAGE"
docker pull "$IMAGE"

# Hand Swarm the digest rather than the tag. Swarm resolves a tag to a digest
# once and will not re-resolve it, so deploying :latest a second time is
# usually a silent no-op - the service reports "updated" while continuing to
# run the old code. The digest changes on every push, so it cannot lie.
DIGEST="$(docker image inspect "$IMAGE" --format '{{index .RepoDigests 0}}' 2>/dev/null || true)"
if [[ -n "$DIGEST" ]]; then
  echo "    digest: $DIGEST"
  IMAGE="$DIGEST"
else
  echo "    (no digest available - image was built locally, deploying by tag)"
fi

echo "==> Deploying stack $STACK"
IMAGE="$IMAGE" \
SUPABASE_URL="$SUPABASE_URL" \
SUPABASE_KEY="$SUPABASE_KEY" \
AUTH_USER="${AUTH_USER:-admin}" \
AUTH_PASSWORD="${AUTH_PASSWORD:-}" \
CANARY_PHONE_ID="${CANARY_PHONE_ID:-}" \
docker stack deploy \
  --with-registry-auth \
  -c docker-compose.yml \
  "$STACK"

echo
docker stack services "$STACK"

# `docker stack services` says the service was updated, not that the new code
# is serving. /version is the only answer that cannot be wrong: its marker
# carries the git SHA the image was built from.
echo
echo "==> Waiting for the new task to serve..."
for i in $(seq 1 20); do
  sleep 3
  MARKER="$(curl -s -m 3 -u "${AUTH_USER:-admin}:${AUTH_PASSWORD:-}" \
    localhost:8090/version 2>/dev/null | sed -n 's/.*"marker" *: *"\([^"]*\)".*/\1/p')"
  if [[ -n "$MARKER" ]]; then
    echo "    running marker: $MARKER"
    exit 0
  fi
done

echo "!! No answer from /version after 60s - check: docker service ps ${STACK}_orchestrator --no-trunc" >&2
exit 1