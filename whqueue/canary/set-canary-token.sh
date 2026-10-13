#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════════════
# מכניס טוקן מטא חדש של טלפון ה-canary ל-docker secret ומעביר אליו את ה-prober.
#
#   ./set-canary-token.sh            מבקש את הטוקן בלי הד (מומלץ — לא נשאר ב-history)
#   ./set-canary-token.sh '<TOKEN>'  או כארגומנט
#
#   1. בודק שהטוקן עובד מול Graph על ה-phone_number_id שלנו — אחרת יוצא, בלי שינוי
#   2. docker secret חדש עם תאריך (secret הוא immutable)
#   3. service update של ה-prober (אם ה-stack כבר רץ)
#   4. CANARY_WA_SECRET ב-canary.env — כדי ש-deploy הבא לא יחזיר את הישן
#   5. מוחק את ה-secret הקודם
# ═══════════════════════════════════════════════════════════════════════════
set -euo pipefail
cd "$(dirname "$0")"

# בלי ארגומנט — קורא מהמקלדת בלי הד, כדי שהטוקן לא יישאר ב-history.
if [[ $# -ge 1 ]]; then
  TOKEN="$1"
else
  read -rsp "Meta token: " TOKEN; echo
fi
[[ -n "$TOKEN" ]] || { echo "empty token" >&2; exit 1; }
[[ -f canary.env ]] || { echo "missing canary.env" >&2; exit 1; }
set -a; source canary.env; set +a
: "${CANARY_PHONE_NUMBER_ID:?}"

SERVICE="canary_canary-prober"
GRAPH="https://graph.facebook.com/${GRAPH_VERSION:-v21.0}"
log() { echo "$(date -u +%FT%TZ) [canary-token] $*"; }

# ── 1. הטוקן עובד? ──────────────────────────────────────────────────────────
resp="$(curl -sS -G "$GRAPH/$CANARY_PHONE_NUMBER_ID" \
          --data-urlencode "fields=display_phone_number,verified_name" \
          --data-urlencode "access_token=$TOKEN")"
if ! grep -q '"display_phone_number"' <<<"$resp"; then
  log "token rejected by Graph - nothing changed: ${resp:0:300}"
  exit 1
fi
log "token ok: $(python3 -c 'import sys,json; d=json.loads(sys.argv[1]); print(d.get("display_phone_number"), "/", d.get("verified_name"))' "$resp")"

# ── 2. secret חדש ───────────────────────────────────────────────────────────
NEW="canary_wa_token_$(date -u +%Y%m%d%H%M%S)"
OLD="${CANARY_WA_SECRET:-}"
printf '%s' "$TOKEN" | docker secret create "$NEW" - >/dev/null
log "created secret $NEW"

# ── 3. ה-prober עובר אליו ───────────────────────────────────────────────────
if docker service inspect "$SERVICE" >/dev/null 2>&1; then
  args=(--quiet --secret-add "source=$NEW,target=canary_wa_token")
  [[ -n "$OLD" ]] && args+=(--secret-rm "$OLD")
  docker service update "${args[@]}" "$SERVICE"
  log "$SERVICE now uses $NEW"
else
  log "$SERVICE not running - ./deploy-canary.sh will use $NEW"
fi

# ── 4. canary.env ───────────────────────────────────────────────────────────
if grep -q '^CANARY_WA_SECRET=' canary.env; then
  sed -i "s/^CANARY_WA_SECRET=.*/CANARY_WA_SECRET=$NEW/" canary.env
else
  printf '\nCANARY_WA_SECRET=%s\n' "$NEW" >> canary.env
fi
grep -n '^CANARY_WA_SECRET=' canary.env
# אם מישהו השאיר את הטוקן עצמו בקובץ — מנקים
sed -i 's/^CANARY_WA_ACCESS_TOKEN=.*/CANARY_WA_ACCESS_TOKEN=/' canary.env

# ── 5. הישן ─────────────────────────────────────────────────────────────────
if [[ -n "$OLD" && "$OLD" != "$NEW" ]]; then
  docker secret rm "$OLD" >/dev/null 2>&1 && log "removed $OLD" \
    || log "$OLD still in use - later: docker secret rm $OLD"
fi
