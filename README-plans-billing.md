# Grossman.Bot: תוכניות, מכסות, חיוב, השכרות ו-Back Office

המסמך מתאר את כל מה שנבנה סביב התוכניות: מכסות לפי תוכנית, תמחור, חיוב בשיטת חוב,
תשלום דרך CardCom, השכרת טלפונים, היסטוריית שינויים, מסך הניהול ב-PhoneOrchestrator ותיקון ההתראות.

שני חבילות:
- **`plans-billing.zip`**: האפליקציה (botbackend ב-FastAPI ו-React), יחד עם `20_plans.sql` ו-`21_billing.sql`.
- **`orchestrator-admin.zip`**: ה-back office ב-PhoneOrchestrator, יחד עם `22_admin.sql`.

---

## 1. ארכיטקטורה

```
React UI ──► botbackend (FastAPI)  ──► Supabase RPC (plans, quota, billing)
               │ owns enforcement          ▲
               │                           │ service_role
PhoneOrchestrator back office ─────────────┘  (/api/admin/* → rpc_admin_*)
Landing page ──► GET /api/plans/public (no login)
CardCom ──► POST /api/billing/cardcom/webhook ──► GetLpResult ──► payment = paid
```

- **האכיפה ב-backend בלבד.** ה-UI רק מסתיר או חוסם כפתורים לפי `GET /plans/me`. השרת מחזיר `409` או `403` בכל מקרה.
- **Spine ו-scheduler לא בודקים מכסות.** ממכסת ההודעות ויתרנו בשלב הזה.
- **הלוגיקה ב-DB:** ספירות, חיובים, דפדוף ואימות נעשים ב-RPC (`rpc_*`), כך שכל קריאה היא round-trip אחד.
- **מקור אחד לתמחור:** טבלאות `plans` ו-`rent_options`. האפליקציה, דף הנחיתה, הדיאלוגים והמכסות קוראים מהן.

---

## 2. תוכניות ומכסות

| תוכנית | מחיר/חודש | טלפונים | בוטים | תזמונים | API | הרצות מקביליות | ניטור |
|---|---|---|---|---|---|---|---|
| free | $0 | 1 | 1 | 1 | ✗ | ✗ | ✗ |
| basic | $49 | 2 | 3 | 10 | ✓ | ✗ | ✓ |
| advanced | $149 | 5 | 10 | ∞ | ✓ | ✓ | ✓ |
| enterprise | מותאם | ∞ | ∞ | ∞ | ✓ | ✓ | ✓ |

אלה ערכי הזרע. המקור האמיתי הוא טבלת `plans`, שנערכת במסך "תוכניות". בטבלה, `null` פירושו ללא הגבלה.

### מה נספר
- **טלפונים:** `phones` של המשתמש, בלי טלפונים מושכרים (אלה עם `sim_id`).
- **בוטים:** אנשי קשר במצב `tag = 'active'` בכל הטלפונים של המשתמש.
- **תזמונים:** `schedules` דרך הבעלים של הטלפון, בלי `completed`.

### איפה נאכף
| משאב | נקודת אכיפה |
|---|---|
| phones | `POST /phones/provision`, רק לטלפון חדש |
| bots | PING (`create-from-ping`), עוד לפני שנשלחת הודעה; PONG (`select-response`); `POST /contacts` עם `active`; `PATCH` במעבר אמיתי ל-`active`; `POST /contacts/{id}/connect` |
| schedules | `POST /schedules` |
| api / loadTest | ב-UI הכפתור והנתיב מוסתרים. בשרת: `Depends(feature_dep("api"))` ו-`feature_dep("loadTest")` (עוד פתוח, ראו §11) |

**שגיאות:**
- **מכסה מלאה:** `409 { code: QUOTA_EXCEEDED, resource, limit, used }`
- **מקום בוט בהמתנה:** `409 { code: QUOTA_COOLDOWN, retry_at, cooldown_hours }`
- **פיצ'ר לא כלול בתוכנית:** `403 { code: PLAN_FEATURE, feature }`

### המתנה אחרי הסרת בוט (מניעת הפצה)
בוט שיוצא ממצב `active` (ביטול, מחיקה, STOP או סיום השכרה) נרשם ב-`contact_releases` על ידי trigger.
המקום שלו נשאר תפוס `plans.bot_cooldown_hours` שעות (ברירת מחדל 24).

```
used + cooling >= limit  and  used < limit   →  QUOTA_COOLDOWN (when a slot frees up)
used >= limit                                →  QUOTA_EXCEEDED (upgrade)
```
כך לקוח לא יכול להסתובב בין בוטים כדי לשלוח הודעות להרבה מספרים.

### תוכנית ברירת מחדל
משתמש שעוד לא בחר תוכנית מקבל את `bot_config plans.default_plan` (`free`), ונרשם ב-`user_plans`
ובהיסטוריה בכניסה הראשונה. אם ברירת המחדל תהיה תוכנית בתשלום, כל משתמש חדש יתחיל בחוב.

---

## 3. תמחור: כללי המחיר

### הכלל: הלקוח תמיד משלם את המחיר הנמוך מבין השניים
עלייה במחיר לא חלה על מה שכבר רץ. ירידה חלה מיד.

| | המחיר שנלקח |
|---|---|
| **תוכנית, לכל חודש** | `min(` המחיר בתחילת החודש, או ביום המעבר לתוכנית אם מאוחר יותר `,` המחיר היום `)` |
| **השכרה** | `min(` המחיר בזמן ההזמנה (`rent.price`) `,` מחיר המשך היום `)` |

**דוגמאות:**
- basic ב-$49, באמצע החודש עולה ל-$59: החודש $49, מהחודש הבא $59.
- basic ב-$49, באמצע החודש יורדת ל-$39: החודש $39 מיד.
- השכרה שהוזמנה ב-$4, המחיר עולה ל-$5: $4. המחיר יורד ל-$3: $3.

### שדרוג וירידה באותו חודש
החודש מחויב לפי **התוכנית היקרה ביותר שהייתה פעילה בו**, כל תוכנית לפי הכלל שלמעלה.
כך שדרוג לכמה ימים ואחריו ירידה עדיין מחויבים כשדרוג.

### חודשים שעברו קפואים
`rpc_billing_accrue` רק מוסיף שורות לחודשים קודמים. שורות של החודש הנוכחי בלבד מתעדכנות.

### "המחיר בזמן X"
נלקח מ-`catalog_history`, שרושמת כל שינוי ב-`plans` וב-`rent_options` דרך trigger.
- אם יש שינוי עד הזמן X: המחיר החדש של השינוי האחרון עד אליו.
- אחרת: המחיר הישן של השינוי הראשון אחריו.
- אחרת: המחיר של היום.

הפונקציה: `_plan_price_at(plan, ts)`.

### השכרה: מחיר לכל משך
טבלת `rent_options` (מסך "השכרות"): `hours`, `price` כולל, `is_active`, `sort`.
- **ברירת מחדל:** 1 שעה $5, 3 שעות $15, 8 שעות $40, 24 שעות $120.
- **השכרה חינמית:** מחיר 0. אז לא נרשם חיוב, והדיאלוג מציג "🎁 ללא תשלום".
- **השכרה כבויה:** אין אף משך פעיל. אז `409 RENT_DISABLED`.
- **משך שלא קיים בטבלה** (השכרות ישנות): לפי התעריף השעתי הזול ביותר.
- **המחיר בזמן ההזמנה** נשמר ב-`rent.price` על ידי `RentDevice.py`.

---

## 4. חיוב: חוב עכשיו, תשלום בהמשך

| טבלה | תוכן |
|---|---|
| `billing_charges` | חיוב לפי חודש: `plan:YYYY-MM` לכל חודש, `rent:<id>` לכל השכרה. ייחודי לפי `(user, kind, ref)` |
| `billing_payments` | תשלומים: `cardcom` (pending עד ה-webhook), `manual` (רישום ידני ב-back office) |
| `billing_methods` | הכרטיס השמור: הטוקן של CardCom, 4 ספרות ותוקף. הטוקן לא יוצא מה-backend |

- **יתרה** = סך החיובים פחות התשלומים במצב `paid`.
- **סטטוס חודש:** התשלומים מכסים את החודשים מהישן לחדש, וכל חודש מסומן `paid`, `partial` או `open`.
- **חישוב:** `rpc_billing_accrue(user)` אידמפוטנטי ובטוח מכמה replicas. הוא רץ כשהלקוח פותח את טאב "חיוב",
  כשהוא משלם, כשפותחים כרטיס לקוח ב-back office, וב"חשב חיובים לכולם".

### CardCom (v11)
1. `POST /billing/checkout`: נוצר תשלום `pending` על כל היתרה, ונקרא `LowProfile/Create`
   (`ChargeAndCreateToken`). הדפדפן עובר לדף התשלום של CardCom.
2. CardCom קורא ל-`POST /billing/cardcom/webhook`. **תוכן הבקשה לא נחשב אמין**: השרת קורא `GetLpResult`
   לפי `LowProfileId`, ורק אחרי זה מסמן את התשלום כ-`paid` ושומר את הטוקן.
3. הלקוח חוזר ל-`/settings/billing?pay=ok|fail`.

**שמות השדות** מתוך `TranzactionInfo` ו-`TokenInfo` נקראים בצורה גמישה ב-`cardcom_client.py`. כדאי לאמת אותם פעם אחת בטרמינל הבדיקות 1000.

**env:**
```
CARDCOM_TERMINAL  CARDCOM_API_NAME  CARDCOM_COIN_ID (2=USD)  CARDCOM_WEBHOOK_URL  APP_URL  PLANS_CURRENCY
```
בלי משתני CardCom, הטאב עובד וכפתור "שלם עכשיו" מציג "עדיין לא זמין".

---

## 5. היסטוריה

| מה | איפה | רואים ב- |
|---|---|---|
| שינויי מחיר, מגבלות ושדות של תוכניות | `catalog_history` (kind=`plan`) | back office ← תוכניות ← היסטוריית שינויים |
| שינויי משך ומחיר של השכרות | `catalog_history` (kind=`rent`) | back office ← השכרות ← היסטוריית שינויים |
| מעבר תוכנית של לקוח | `user_plan_history` (`source`: default / dialog / `admin · admin@IP`) | כרטיס לקוח ← היסטוריית תוכניות |
| השכרות של לקוח | `rent` + `billing_charges` | כרטיס לקוח ← השכרות, back office ← השכרות |

`catalog_history` שומרת `old_row`, `new_row`, את העמודות שהשתנו, את הזמן ואת `actor`.
- **שינוי מה-back office:** `admin@<IP>`, כי לדשבורד יש סיסמה אחת ואין שם משתמש.
- **שינוי מה-SQL editor:** שם ה-role של ה-DB.

---

## 6. ממשק המשתמש (אפליקציה)

| מקום | מה |
|---|---|
| סרגל עליון | 💎 שם התוכנית, פותח את `PlanDialog`. API מוצג רק עם `api`, הרצות מקביליות רק עם `loadTest` |
| `PlanDialog` | שימוש מול מכסה, כרטיסי תוכניות, אזהרה לפני ירידה, אישור. נסגר לבד אחרי החלפה. מרונדר דרך portal מעל ההגדרות |
| הגדרות ← תוכנית | תוכנית נוכחית, מחיר, פסי שימוש, "החלף תוכנית" |
| הגדרות ← חיוב | יתרה, "שלם עכשיו" (CardCom), כרטיס שמור, חיובים לפי חודש עם פירוט, תשלומים |
| הגדרות (כללי) | אחרי כל פעולה מוצגת תווית "✓ … · נסגר…" עם פס של 2 שניות וכפתור "השאר פתוח" |
| טלפונים, אנשי קשר, תזמונים | `QuotaBanner`: הודעת מכסה או המתנה, עם כפתור שדרוג. כפתורי ההוספה חסומים |
| השכרת טלפון | בחירת משך עם מחיר, "חינם" כשהמחיר 0, ו"נכנס ליתרה" כשיש מחיר |
| דף נחיתה | התמחור מ-`GET /plans/public`, ו-`public/pricing.json` רק כגיבוי |
| פעמון התראות | **תוקן:** מציג רק את ההתראות של המשתמש. נוסף "🗑 מחק הכל" |

**i18n:** `features/plan.json`, `notif.json` ו-`billing.json`. כל הטקסטים דרך `T("literalKey")`.

---

## 7. Back Office (PhoneOrchestrator)

| דף | מה |
|---|---|
| **תוכניות** (`/plans.html`) | עריכה ושמירה לכל שורה, תוכנית חדשה, מספר לקוחות, היסטוריית שינויים |
| **השכרות** (`/rentals.html`) | משכים ומחירים, רשימת השכרות (פעילות / הסתיימו / הכל, חיפוש), היסטוריית שינויים |
| **לקוחות** (`/customers.html`) | רשימה עם דפדוף (החוב הגדול ראשון), חיפוש, סינון לפי תוכנית, "בחוב בלבד", סה"כ חוב פתוח, "חשב חיובים לכולם" |
| כרטיס לקוח | שימוש, יתרה, כרטיס שמור, החלפת תוכנית, תשלום ידני (סכום שלילי מבטל), היסטוריית תוכניות, השכרות, חיובים לפי חודש, תשלומים |
| **הגדרות** (`/config.html`) | `bot_config` עם דפדוף וחיפוש, עריכה של ערך והסבר |

- **Controller:** `AdminController.cs` תחת `/api/admin/*`, מאחורי `AuthMiddleware`. כל שינוי נכתב ל-log ברמת Warning.
- **רשימות:** חוזרות כ-`{ items: [...] }`, כי `SupabaseRpc.Unwrap` לוקח את האיבר הראשון ממערך.
- **גודל עמוד:** `bot_config admin.page_size`.

---

## 8. bot_config

| מפתח | ברירת מחדל | משמעות |
|---|---|---|
| `plans.default_plan` | `free` | תוכנית למשתמש שעוד לא בחר |
| `plans.bot_cooldown_hours` | `24` | כמה שעות מקום של בוט שהוסר נשאר תפוס. 0 = ללא המתנה |
| `admin.page_size` | `25` | שורות בעמוד ב-back office (5–200) |
| `rent.hours` | (קיים) | משך ברירת המחדל להשכרה, רק אם הוא משך פעיל ב-`rent_options` |

מחירי ההשכרה **לא** נמצאים ב-`bot_config`, אלא ב-`rent_options`.

---

## 9. אבטחה

- **RLS:** מופעל על `plans`, `user_plans`, `user_plan_history`, `contact_releases`, `billing_*`, `rent_options`
  ו-`catalog_history`, בלי policies. רק service_role ניגש אליהן.
- **`rpc_admin_*`, `rpc_billing_*`:** SECURITY DEFINER, ופתוחים ל-service_role בלבד.
- **`rpc_plan_usage`:** service_role יכול לשאול על כל משתמש. משתמש מחובר רק על עצמו (APP_MODE=client).
- **התראות:** כל endpoint מסונן לפי ה-`uid` מהטוקן.
- **טוקן הכרטיס:** נשמר רק ב-`billing_methods`, ומוסתר (`***`) ב-`raw` של התשלום.

---

## 10. סדר פריסה

0. **החלטה:** האם ההשכרה חינמית. אם כן, אחרי שלב 1 לקבוע מחיר 0 במסך "השכרות".
1. **SQL:** `20_plans.sql`, אחר כך `21_billing.sql`, ובסוף `22_admin.sql`. אפשר להריץ שוב בלי נזק, אחרי כל עדכון.
2. **botbackend:** routers ו-`main.py` (גרסה 2.0.0.55). `plans` ו-`billing` נרשמים במצב `internal` בלבד. להגדיר את משתני ה-env.
3. **i18n:** לדחוף את שלושת קבצי `features/*.json` ל-DB.
4. **frontend:** build עם tag חדש.
5. **PhoneOrchestrator:** `AdminController.cs` ל-`src/Controllers`, הדפים ל-`src/wwwroot`, `patch_nav.py src/wwwroot`, `dotnet build`, deploy.

---

## 11. פתוח

- [ ] **`feature_dep("loadTest")`** על ראוטר ה-executions, ו-**`feature_dep("api")`** על ראוטרי ה-API החיצוני.
- [ ] **`schedules_list` / `rpc_schedules_list`:** בלי `phone_id` מחזירים תזמונים של כל המשתמשים, ו-update/delete לא בודקים בעלות.
- [ ] **CardCom:** אימות שמות השדות בטרמינל 1000, וחיוב חודשי אוטומטי מהטוקן השמור (job ב-scheduler).
- [ ] **Back office ← השכרות:** פעולות סיום, הארכה ומחיקת חיוב.
- [ ] **`ConnectContactModal`:** מציג "חבר" ל-`inactive`, אבל השרת מחבר רק `new` / `draft`.
