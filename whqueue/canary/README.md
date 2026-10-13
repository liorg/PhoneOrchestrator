# Canary — בדיקת תקינות יומית לטלפונים

טלפון Cloud API ייעודי (ה-**prober**) שולח לכל טלפון פעיל תבנית עם קוד, והטלפון עונה אוטומטית `ACK <קוד>`.
טלפון שלא ענה — נרשם ככושל, והבעלים מקבל התראה.

---

## 1. איך זה בנוי

```
botinbound (swarm, 10.128.0.5)                          michal (swarm נפרד)
┌───────────────────────────────────────────────┐      ┌───────────────────────┐
│ stack canary   (/opt/whqueue/repo/canary)      │      │ stack orchestrator     │
│   canary-runner   קורא canary_control, מריץ סבב │      │   מסך ניהול           │
│   canary-prober   whatsapp-cloudapi, 972507251926│     │   canary.html          │
│   canary-redis    webhook store של ה-prober     │      └──────────┬────────────┘
│ stack whqueue  (stack.yml — לא נוגעים)          │                 │
│   whqueue, nats-1                               │                 │
└───────────────┬───────────────────────────────┘                 │
                └────────────── Supabase ─────────────────────────┘
```

- שני השרתים **לא** מדברים ביניהם — רק דרך Supabase (`canary_control`, `canary_runs`, `canary_checks`, `notifications`).
- ה-prober **רק** ב-botinbound. ב-michal אין prober.
- שום שירות canary לא מפורסם החוצה.

### מהלך סבב

1. runner → prober: `send/template check_id` עם קוד ייחודי, לכל טלפון `phones.status='active'`
2. Meta → הטלפון הנבדק (Baileys) → Manager מזהה `CHECK ID: <קוד>` משולח = ה-prober
3. Manager עונה מאותו טלפון: `ACK <קוד>`
4. Meta → whqueue → NATS → prober → runner → `canary_checks.status = passed`

---

## 2. רכיבים והגדרות

| רכיב | איפה | הגדרה |
|---|---|---|
| canary-runner | botinbound | `liorgr/phone-orchestrator`, `Orchestrator__Mode=canary` |
| canary-prober | botinbound | `liorgr/whatsapp-cloudapi` |
| מסך ניהול | michal | `liorgr/phone-orchestrator`, `Mode=full`, `CanaryEnabled=false` |
| auto-reply | Manager (`/opt/myapp`) | `AppSettings__Canary__ProberNumbers=972507251926` |
| טלפון ה-canary | `phones` | `e4ab3a7f-…` · `+972507251926` · `status=inactive` · `docker_status=stopped` · host `botinbound` (`kind=canary`) |
| תבנית | WABA `1119020710931381` | `check_id` · `en` · UTILITY · גוף: `Automated service health check. CHECK ID: {{1}} . No action is needed.` |

> הטקסט `CHECK ID: {{1}}` בתבנית הוא מה שה-Manager מחפש. משנים נוסח — משאירים אותו.

### קבצים (`/opt/whqueue/repo/canary`, repo `liorg/whqueue`)

| קובץ | מה |
|---|---|
| `canary.yml` | ה-stack. **בלי** `${VAR:?msg}` — docker stack מפרש אותו לא נכון |
| `canary.env` | ערכים. **לא ב-git**, `chmod 600` |
| `deploy-canary.sh` | בודק שדות ריקים, secrets, pull לפי digest, `stack deploy` |
| `set-canary-token.sh` | מכניס טוקן Meta ל-docker secret |

### סודות — docker secret, לא env

| secret | מה | איך מחליפים |
|---|---|---|
| `canary_wa_token_<תאריך>` | טוקן Meta | `./set-canary-token.sh` |
| `canary_supabase_key` | service-role key של ה-runner | ידנית, ראה §7 |

`CANARY_WHQUEUE_PHONE_TOKEN` נגזר מ-`whqueue_master` — לא משתנה כל עוד ה-master וה-`phone_id` לא משתנים.

---

## 3. שליטה — טבלת `canary_control` (שורה `id=1`)

ה-runner קורא אותה כל 30 שניות. שינוי נכנס לתוקף בלי deploy.

| שדה | עכשיו | משמעות |
|---|---|---|
| `enabled` | `false` | `false` = אין סבבים בכלל |
| `interval_sec` | `72000` | 20 שעות בין סבבים |
| `active_from` / `active_to` | `10:00` / `11:00` | חלון יומי (`timezone`) → **סבב אחד ביום ב-10:00** |
| `timeout_sec` | `120` | המתנה ל-ACK |
| `check_cloudapi` | `false` | טלפוני Cloud API → `skipped` |
| `notify` | `true` | התראות ב-`notifications` |
| `run_now_at` | — | המסך מציב → סבב אחד מיד, גם כשכבוי |

ה-runner כותב לאותה שורה: `runner_seen_at`, `runner_mode`, `next_sweep_at`, `last_result`, `prober_ready`, `prober_health`.

### הפעלה / עצירה

- **מסך**: `orch.grossman.bot/canary.html` → "הפעל" / "עצור" / "הרץ סבב עכשיו"
- **SQL**: `update canary_control set enabled = true|false where id = 1;`

עצירה חזקה יותר (botinbound):
```bash
sudo docker service scale canary_canary-runner=0     # חזרה: =1
sudo docker stack rm canary                          # חזרה: sudo ./deploy-canary.sh
```
ניטרול התשובות ב-Manager: `AppSettings__Canary__AutoReply=false` + restart.

---

## 4. תוצאות

### `canary_checks.status`

| status | משמעות | התראה |
|---|---|---|
| `passed` | ACK מהמספר הנכון בזמן | חזרה לתקינות — אם קודם נכשל |
| `timeout` + `delivery_status ≥ 2` | הגיע לטלפון, לא חזר ACK → Manager / auto-reply | כן |
| `timeout` + `delivery_status` ריק/1 | לא הגיע → הטלפון מנותק מ-WhatsApp | כן |
| `late_ack` | ACK אחרי ה-timeout — חי אבל איטי | — |
| `failed` | ACK ממספר אחר (באג ניתוב) | כן |
| `send_failed` | Meta דחתה (תבנית/טוקן) — לא אשמת הטלפון | לא |
| `skipped` | Cloud API | לא |

### התראות (`notifications`, `source='canary'`)

רק **במעבר מצב**: תקין→נכשל ("בדיקת תקינות נכשלה"), נכשל→תקין ("הטלפון חזר לתקינות").
`user_id` = בעל הטלפון (`phones.user_id`). טלפון שנשאר מנותק — התראה אחת בלבד.

### סבב שלא רץ

`canary_runs.status = 'aborted'` + `error = prober not ready: <סיבה>` — ה-prober לא תקין (טוקן, NATS, webhook). לקוחות לא מקבלים התראה.

```sql
select started_at, status, total, passed, failed, skipped, error
from canary_runs order by started_at desc limit 10;

select phone_number, status, delivery_status, latency_ms, error
from canary_checks where run_id = '<run>' order by status;
```

---

## 5. בדיקות מהירות (botinbound)

```bash
R="$(sudo docker ps -q -f label=com.docker.swarm.service.name=canary_canary-runner | head -1)"

sudo docker service ls --filter name=canary_                                  # 3 × 1/1
sudo docker exec "$R" curl -s http://127.0.0.1:8080/version; echo             # 1.1.0
sudo docker exec "$R" curl -s http://canary-prober:8000/health | python3 -m json.tool
sudo docker exec "$R" curl -s http://canary-prober:8000/templates/check_id; echo
sudo docker service logs canary_canary-runner --since 10m 2>&1 | grep -i canary
sudo docker service logs canary_canary-prober --since 10m 2>&1 | grep -vE "GET /health|graph.facebook" | tail -20
```

`/health` תקין:
`connected`, `token_ok`, `registered`, `nats_connected`, `override_set` = `true` · `webhooks_count = 1` · `redis_connected = true`

ב-michal:
```bash
curl -s localhost:8090/version; echo
sudo bash -c 'source .env; curl -s -u "${AUTH_USER:-admin}:$AUTH_PASSWORD" localhost:8090/api/canary/control' | python3 -m json.tool | grep -E 'runnerAlive|runner_mode|prober_ready|enabled'
```

### בדיקה ידנית של השרשרת הנכנסת
מכל טלפון שולחים ל-+972 50-725-1926: `ACK TEST1234` →
```bash
sudo docker service logs canary_canary-runner --since 1m 2>&1 | grep ACK
# [CANARY] ACK TEST1234 matches no check   ← תקין
```

---

## 6. פריסה ועדכון

### עדכון קוד (image)
1. push ל-`main` של `liorg/PhoneOrchestrator` → GitHub Actions בונה `liorgr/phone-orchestrator:latest`
2. michal: `cd /opt/phone-orchestrator && git pull && sudo ./deploy.sh`
3. botinbound: `cd /opt/whqueue/repo/canary && sudo ./deploy-canary.sh`

### עדכון קבצי canary
1. commit + push ל-`liorg/whqueue` (`canary/`)
2. botinbound: `cd /opt/whqueue/repo && git pull && cd canary && sudo ./deploy-canary.sh`

> **git בלי sudo, deploy/docker עם sudo.** `git config core.fileMode false` — שלא ייתקע על chmod.

---

## 7. תחזוקה

### חידוש טוקן Meta
1. Business Manager → System users → Generate new token (הרשאות `whatsapp_business_messaging`, `whatsapp_business_management`)
2. botinbound:
   ```bash
   cd /opt/whqueue/repo/canary && sudo ./set-canary-token.sh     # מבקש טוקן בלי הד
   ```
   בודק מול Graph → secret חדש → ה-prober עובר אליו → הישן נמחק. בלי deploy.

### החלפת service-role key
```bash
KEY="$(grep -E '^SUPABASE_SERVICE_ROLE_KEY=' /opt/whqueue/whqueue.env | cut -d= -f2- | tr -d '\r"'\''')"
sudo docker service update --quiet --secret-rm canary_supabase_key canary_canary-runner
sudo docker secret rm canary_supabase_key
printf '%s' "$KEY" | sudo docker secret create canary_supabase_key - >/dev/null
sudo docker service update --quiet --secret-add source=canary_supabase_key,target=supabase_key canary_canary-runner
unset KEY
```

### `CANARY_WHQUEUE_PHONE_TOKEN` מחדש
רק אם `whqueue_master` או `WHQUEUE_TOKEN_VERSION` השתנו:
```bash
CID="$(sudo docker ps -q -f label=com.docker.swarm.service.name=whqueue_whqueue | head -1)"
TOK="$(sudo docker exec "$CID" python3 -c 'from whqueue.security import phone_token; print(phone_token("e4ab3a7f-08bb-4e12-aea9-fd34279beb99"))')"
sed -i "s/^CANARY_WHQUEUE_PHONE_TOKEN=.*/CANARY_WHQUEUE_PHONE_TOKEN=$TOK/" canary.env; unset TOK
sudo ./deploy-canary.sh
```

---

## 8. תקלות

| סימפטום | סיבה | פתרון |
|---|---|---|
| `secret not found: canary-token.sh first` | `${VAR:?msg}` ב-canary.yml | `sed -i -E 's/\$\{([A-Z_]+):\?[^}]*\}/${\1}/g' canary.yml` |
| runner: `401 Invalid API key` | `canary_supabase_key` עם מרכאות/שגוי | §7 החלפת service-role key |
| `runnerAlive: false` במסך | ה-runner למטה / לא כותב ל-DB | `docker service ps canary_canary-runner`, לוגים |
| `prober_ready: false`, `token_ok=false` | טוקן Meta נשלל/פג | §7 חידוש טוקן |
| `nats_connected=false` | whqueue `/register` נכשל | לוג prober; בדיקת `/register` עם `CANARY_WHQUEUE_PHONE_TOKEN` |
| `webhooks_count=0` | ה-prober לא יודע לאן לשלוח | redis; `MANAGER_WEBHOOK_URL` ב-canary.yml |
| כל הבדיקות `send_failed` | תבנית לא מאושרת / שם-שפה שגויים | `/templates/check_id`, `template_lang` |
| `timeout` + נמסר, אין ACK | Manager לא עונה | build עם `CanaryAutoReply`; `ProberNumbers` ב-.env של ה-Manager |
| `git pull`: local changes | chmod / עריכה ידנית | `git checkout -- canary/` (canary.env לא נפגע) |
| `git`: dubious ownership | git עם sudo על repo של משתמש | git בלי sudo |