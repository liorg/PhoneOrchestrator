#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"

for f in .env deploy.env; do
  if [[ -f "$f" ]]; then
    set -a
    source "$f"
    set +a
  fi
done

: "${IMAGE_NAME:?set IMAGE_NAME in .env}"
: "${SUPABASE_URL:?set SUPABASE_URL in .env}"
: "${SUPABASE_KEY:?set SUPABASE_KEY in .env}"

IMAGE_TAG="${IMAGE_TAG:-$(date +%Y%m%d-%H%M%S)}"
STACK="${STACK:-orchestrator}"

if [[ -n "${REGISTRY:-}" ]]; then
  IMAGE="$REGISTRY/$IMAGE_NAME:$IMAGE_TAG"
else
  IMAGE="$IMAGE_NAME:$IMAGE_TAG"
fi

echo "==> build $IMAGE"
docker build -t "$IMAGE" .

if [[ -n "${REGISTRY:-}" ]]; then
  echo "==> push"
  docker push "$IMAGE"
  echo "==> warm image cache on every node"
  for node in $(docker node ls --format '{{.Hostname}}'); do
    echo "    $node"
    docker -H "ssh://$node" pull "$IMAGE" 2>/dev/null \
      || echo "    (skipped - no ssh to $node)"
  done
else
  echo "==> single node, image stays local"
fi

echo "==> deploy stack $STACK"
IMAGE="$IMAGE" SUPABASE_URL="$SUPABASE_URL" SUPABASE_KEY="$SUPABASE_KEY" \
  docker stack deploy -c docker-compose.yml "$STACK"

echo "==> deployed $IMAGE"