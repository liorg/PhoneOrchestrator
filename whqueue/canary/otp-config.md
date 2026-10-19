# הגדרות OTP

עודכן 2026-10-19. פירוט מלא של הזרימה: `otp-2fa.md`.

---

## backend — michal (`/opt/scenario-stack/.env`)

שירות `backend` ב-`stack.yml` (`APP_MODE=internal`).

| משתנה | ברירת מחדל | חובה | משמעות |
|---|---|---|---|
| `OTP_PEPPER` | — | **כן** | מפתח HMAC לאימות הקוד. `openssl rand -hex 32`. **לא לשנות** — מבטל קודים פתוחים |
| `OTP_TTL_SEC` | `420` | | תוקף קוד בשניות (7 דקות) |
| `OTP_RESEND_SEC` | `60` | | המתנה מינימלית בין בקשות קוד. רק שליחה שהצליחה נספרת |
| `OTP_MAX_ATTEMPTS` | `5` | | הקלדות שגויות מותרות לקוד אחד |
| `OTP_MAX_PER_HOUR` | `5` | | קודים לשעה למשתמש, כולל שליחות שנכשלו |
| `PHONE_ADMINS` | `g@miichal-solutions.com` | | מי רשאי להוסיף טלפון (פסיקים). בנוסף ל-2FA |

הוספה:
```bash
cd /opt/scenario-stack
grep -q '^OTP_PEPPER=' .env || echo "OTP_PEPPER=$(openssl rand -hex 32)" | sudo tee -a .env >/dev/null
sudo chmod 600 .env
```

> ערך בשורה נפרדת, בלי הערה (`#`) באותה שורה.

קבוע בקוד (`routers/two_factor.py`): `SEND_WAIT_SEC = 8` — כמה זמן `request` ממתין ל-`sent`/`failed` לפני שמחזיר 202.

---

## PhoneOrchestrator — botinbound (`canary.yml`, שירות `canary-runner`)

**אין חובה להגדיר כלום.** `OtpSender` עולה לבד ב-`Orchestrator__Mode=canary`.

| משתנה | ברירת מחדל | משמעות |
|---|---|---|
| `Otp__Enabled` | `true` | `false` מכבה את שליחת ה-OTP |
| `Otp__Template` | `check_number` | שם התבנית המאושרת ב-WABA של ה-prober |
| `Otp__Language` | `en` | שפת התבנית |
| `Otp__Body` | `{name}\|{code}` | פרמטרי BODY לפי הסדר: `{{1}}`, `{{2}}`, ... |
| `Otp__Header` | ריק | פרמטרי HEADER. ריק = לתבנית אין משתנים ב-header |
| `Otp__PollSec` | `2` | כל כמה שניות לבדוק את `otp_requests` |

Placeholders זמינים ב-`Body` / `Header`: `{code}` · `{name}` · `{mobile}`. מפרידים ב-`|`.

נלקח מההגדרות הקיימות של ה-runner — לא לגעת:

| | מקור |
|---|---|
| Supabase URL | `Orchestrator:SupabaseUrl` (`/run/secrets/supabase_url` או env) |
| Supabase key | `Orchestrator:SupabaseKey` (`/run/secrets/supabase_key`, service role) |
| כתובת ה-prober | `Orchestrator:CanaryProberUrl` → `http://canary-prober:8000` |

---

## התבנית — `check_number`

| | |
|---|---|
| WABA | `1119020710931381` (ה-prober, `+972507251926`) |
| שפה | `en` |
| קטגוריה | UTILITY |
| סטטוס | `approved` |
| HEADER | `Hello Dear Customer` — בלי משתנים |
| BODY | `Hello {{1}}, ... is {{2}}.` → `{{1}}`=שם, `{{2}}`=קוד |

**החלפת תבנית** — רק ב-`canary.yml`, בלי שינוי קוד:
```yaml
Otp__Template: otp_code
Otp__Language: he
Otp__Body: "{code}"
```
ואז `sudo ./deploy-canary.sh`. התבנית חייבת להיות `approved` ב-WABA של ה-prober.

בדיקה:
```bash
R="$(sudo docker ps -q -f label=com.docker.swarm.service.name=canary_canary-runner | head -1)"
sudo docker exec "$R" curl -s http://canary-prober:8000/templates/check_number; echo
```

---

## Frontend

| משתנה | משמעות |
|---|---|
| `VITE_API_URL` | חייב להצביע על `backend` (`https://backend.grossman.bot/api`), לא על `grossman-api` |
| `VITE_PHONE_ADMINS` | fallback בלבד לזמן הטעינה. מקור האמת: `/phones/can_add` |

---

## בדיקת ערכים בפועל

```bash
# michal
sudo grep -E '^OTP_|^PHONE_ADMINS' /opt/scenario-stack/.env | sed 's/\(OTP_PEPPER=\).*/\1***/'

# botinbound
sudo docker service inspect canary_canary-runner \
  --format '{{range .Spec.TaskTemplate.ContainerSpec.Env}}{{println .}}{{end}}' | grep -E '^Otp__|Mode'
sudo docker service logs canary_canary-runner --since 10m 2>&1 | grep '\[OTP\] started'
# [OTP] started | template=check_number/en poll=2s
```
