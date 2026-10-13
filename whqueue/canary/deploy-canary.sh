#!/usr/bin/env bash
# Pull + deploy של stack ה-canary. לא נוגע ב-whqueue.
set -euo pipefail
cd "$(dirname "$0")"

[[ -f canary.env ]] || { echo "missing canary.env (cp canary.env.example canary.env)" >&2; exit 1; }
set -a; source canary.env; set +a

# ── כל מה ש-canary.yml צריך — נבדק כאן ולא ב-yml ─────────────────────────
missing=()
for v in SUPABASE_URL WHQUEUE_VERIFY_TOKEN CANARY_HOST_ID CANARY_PHONE_ID CANARY_PHONE_NUMBER \
         CANARY_PHONE_NUMBER_ID CANARY_WABA_ID CANARY_WHQUEUE_PHONE_TOKEN CANARY_WA_SECRET \
         CANARY_INBOUND_TOKEN CANARY_RUNNER_PASSWORD; do
  [[ -n "${!v:-}" ]] || missing+=("$v")
done
if (( ${#missing[@]} )); then
  echo "!! empty in canary.env: ${missing[*]}" >&2
  exit 1
fi

# ── secrets ────────────────────────────────────────────────────────────────
# טוקן מטא: נוצר רק ב-set-canary-token.sh.
if [[ -z "${CANARY_WA_SECRET:-}" ]] || ! docker secret inspect "$CANARY_WA_SECRET" >/dev/null 2>&1; then
  echo "!! no Meta token secret - run: ./set-canary-token.sh '<TOKEN>'" >&2
  exit 1
fi

# service-role key: נוצר פעם אחת מ-canary.env, ואז אפשר למחוק את השורה משם.
if ! docker secret inspect canary_supabase_key >/dev/null 2>&1; then
  : "${SUPABASE_SERVICE_ROLE_KEY:?canary_supabase_key secret missing - set SUPABASE_SERVICE_ROLE_KEY once}"
  printf '%s' "$SUPABASE_SERVICE_ROLE_KEY" | docker secret create canary_supabase_key - >/dev/null
  echo "==> created secret canary_supabase_key"
fi

# המפתח כבר ב-secret — לא משאירים עותק על הדיסק.
if grep -q '^SUPABASE_SERVICE_ROLE_KEY=.\+' canary.env; then
  sed -i 's/^SUPABASE_SERVICE_ROLE_KEY=.*/SUPABASE_SERVICE_ROLE_KEY=/' canary.env
  echo "==> cleared SUPABASE_SERVICE_ROLE_KEY in canary.env (lives in secret canary_supabase_key)"
fi

# ── images לפי digest — Swarm לא מושך מחדש tag שכבר פתר ─────────────────
pin() {   # repo tag → tag@sha256:…
  local ref="$1:$2"
  docker pull -q "$ref" >/dev/null
  echo "$2@$(docker image inspect "$ref" --format '{{index .RepoDigests 0}}' | cut -d@ -f2)"
}
export ORCH_TAG="$(pin liorgr/phone-orchestrator "${ORCH_TAG:-latest}")"
export CLOUDAPI_TAG="$(pin liorgr/whatsapp-cloudapi "${CLOUDAPI_TAG:-latest}")"
echo "==> phone-orchestrator:$ORCH_TAG"
echo "==> whatsapp-cloudapi:$CLOUDAPI_TAG"

docker stack deploy -c canary.yml --with-registry-auth canary
sleep 5
docker stack services canary
