#!/usr/bin/env bash
# Build, push and deploy PhoneOrchestrator. No node addresses baked in.
set -euo pipefail

cd "$(dirname "$0")"

# deploy.env is the orchestrator's own config; .env is the shared host file
# already present on the VMs. Either works - deploy.env wins if both exist.
for f in .env deploy.env; do
  if [[ -f "$f" ]]; then
    set -a
    # shellcheck disable=SC1090
    source "$f"
    set +a
  fi
done

: "${IMAGE_NAME:?set IMAGE_NAME in .env}"

# REGISTRY is optional. Unset = single-node swarm: the image is built on the
# same machine that runs it, so there is nothing to push to and nothing to
# pull from. Set it later when a second node joins.
: "${SUPABASE_URL:?set SUPABASE_URL in .env or deploy.env}"
: "${SUPABASE_KEY:?set SUPABASE_KEY in .env or create the supabase_key swarm secret}"

# Timestamped by default: Swarm will not re-pull a tag it has already resolved.
IMAGE_TAG="${IMAGE_TAG:-$(date +%Y%m%d-%H%M%S)}"
STACK="${STACK:-orchestrator}"

if [[ -n "${REGISTRY:-}" ]]; then
  IMAGE="$REGISTRY/$IMAGE_NAME:$IMAGE_TAG"
else
  IMAGE="$IMAGE_NAME:$IMAGE_TAG"
fi

BUILD_MARKER="$(git rev-parse --short HEAD 2>/dev/null || echo unknown)-$IMAGE_TAG"

echo "==> build $IMAGE  ($BUILD_MARKER)"
docker build --build-arg BUILD_MARKER="$BUILD_MARKER" -t "$IMAGE" .

if [[ -n "${REGISTRY:-}" ]]; then
  echo "==> push"
  docker push "$IMAGE"

  # Swarm resolves the tag to a digest at deploy time, but a node can only
  # start a task if it can reach the registry or already has the layers.
  # Warming every node removes that dependency at failover time - which is
  # the whole point of this service.
  echo "==> warm image cache on every node"
  for node in $(docker node ls --format '{{.Hostname}}'); do
    echo "    $node"
    docker -H "ssh://$node" pull "$IMAGE" 2>/dev/null \
      || echo "    (skipped - no ssh access to $node, it will pull on demand)"
  done
else
  echo "==> single node, image stays local (no registry configured)"
fi

echo "==> deploy stack $STACK"
IMAGE="$IMAGE" \
  SUPABASE_URL="$SUPABASE_URL" SUPABASE_KEY="$SUPABASE_KEY" \
  AUTH_USER="${AUTH_USER:-admin}" AUTH_PASSWORD="${AUTH_PASSWORD:-}" \
  docker stack deploy -c docker-compose.yml "$STACK"

echo "==> deployed $IMAGE"
echo "    expected marker: $BUILD_MARKER"
echo "    verify:  curl -s localhost:8090/version | jq -r .marker"
echo "    verify: curl -s http://<any-node>:8090/version"
