#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"

# Load environment
# .env first, deploy.env overrides it if it exists
for f in .env deploy.env; do
  if [[ -f "$f" ]]; then
    set -a
    # shellcheck disable=SC1090
    source "$f"
    set +a
  fi
done

# Required configuration
: "${SUPABASE_URL:?set SUPABASE_URL in .env or deploy.env}"
: "${SUPABASE_KEY:?set SUPABASE_KEY in .env or deploy.env}"

# Docker Hub image
IMAGE_NAME="${IMAGE_NAME:-liorgr/phone-orchestrator}"
IMAGE_TAG="${IMAGE_TAG:-latest}"

STACK="${STACK:-orchestrator}"

IMAGE="${IMAGE_NAME}:${IMAGE_TAG}"

echo "========================================"
echo " PhoneOrchestrator Deploy"
echo "========================================"
echo "Stack : $STACK"
echo "Image : $IMAGE"
echo

echo "==> pulling image"
docker pull "$IMAGE"

echo
echo "==> deploying stack $STACK"

IMAGE="$IMAGE" \
SUPABASE_URL="$SUPABASE_URL" \
SUPABASE_KEY="$SUPABASE_KEY" \
AUTH_USER="${AUTH_USER:-admin}" \
AUTH_PASSWORD="${AUTH_PASSWORD:-}" \
docker stack deploy \
  --with-registry-auth \
  -c docker-compose.yml \
  "$STACK"

echo
echo "==> services"
docker stack services "$STACK"

echo
echo "==> deploy completed"
echo "Image: $IMAGE"

echo
echo "Verify:"
echo "  docker service ps ${STACK}_orchestrator"
echo "  docker service logs --tail 50 ${STACK}_orchestrator"
echo "  curl -s http://localhost:8090/version"