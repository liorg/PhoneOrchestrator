#!/usr/bin/env bash
# Build, push and deploy PhoneOrchestrator. No node addresses baked in.
set -euo pipefail

cd "$(dirname "$0")"

if [[ -f deploy.env ]]; then
  # shellcheck disable=SC1091
  source deploy.env
fi

: "${REGISTRY:?set REGISTRY in deploy.env - see deploy.env.example}"
: "${IMAGE_NAME:?set IMAGE_NAME in deploy.env}"
: "${SUPABASE_URL:?set SUPABASE_URL in deploy.env}"

# Timestamped by default: Swarm will not re-pull a tag it has already resolved.
IMAGE_TAG="${IMAGE_TAG:-$(date +%Y%m%d-%H%M%S)}"
STACK="${STACK:-orchestrator}"

IMAGE="$REGISTRY/$IMAGE_NAME:$IMAGE_TAG"

echo "==> build $IMAGE"
docker build -t "$IMAGE" .

echo "==> push"
docker push "$IMAGE"

# Swarm resolves the tag to a digest at deploy time, but a node can only start
# a task if it can reach the registry or already has the layers. Warming every
# node removes the dependency at failover time - which is the whole point of
# this service.
echo "==> warm image cache on every node"
for node in $(docker node ls --format '{{.Hostname}}'); do
  echo "    $node"
  docker -H "ssh://$node" pull "$IMAGE" 2>/dev/null \
    || echo "    (skipped - no ssh access to $node, it will pull on demand)"
done

echo "==> deploy stack $STACK"
REGISTRY="$REGISTRY" IMAGE_NAME="$IMAGE_NAME" IMAGE_TAG="$IMAGE_TAG" \
  SUPABASE_URL="$SUPABASE_URL" \
  docker stack deploy -c docker-compose.yml "$STACK"

echo "==> deployed $IMAGE"
echo "    verify: curl -s http://<any-node>:8090/version"