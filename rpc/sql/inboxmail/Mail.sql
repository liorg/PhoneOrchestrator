-- mail_all.sql — all the mail SQL (11+12+13+14+15) in one file. Idempotent: safe to run again.

-- ════════════════════════ 11_mail_inbound.sql ════════════════════════
-- ════════════════════════════════════════════════════════════════════════════
-- 11_mail_inbound.sql — mail_expect: מייל נכנס → טלפון → Worker
--
-- Cloudflare Email Worker → whqueue /mail → NATS mail.in → whqueue mail-relay
--   → rpc_mail_inbound (שמירה + dedupe + מיפוי נמען → phone + Manager)
--   → HostAgent POST /api/webhook/mail-event/{phoneId}
--   → rpc_mail_forwarded (forwarded_at / forward_error)
--
-- idempotent. service_role בלבד.
-- מיפוי: +tag שהוא phone id קיים (otp+<phone uuid>@grossmanbot.download),
--        ואחרת mailboxes לפי הכתובת בלי ה-+tag
-- ════════════════════════════════════════════════════════════════════════════

create table if not exists public.mailboxes (
  id          uuid primary key default gen_random_uuid(),
  address     text not null unique,                 -- lower-case, בלי +tag
  phone_id    uuid not null references public.phones(id) on delete cascade,
  user_id     uuid references public.users(id) on delete set null,
  is_active   boolean not null default true,
  created_at  timestamptz not null default now(),
  constraint mailboxes_address_lower check (address = lower(address) and position('+' in address) = 0)
);

create table if not exists public.mail_inbound (
  id            uuid primary key default gen_random_uuid(),
  msg_id        text not null unique,               -- Message-ID (או hash שה-Worker מחשב כשחסר)
  phone_id      uuid references public.phones(id) on delete set null,
  mail_to       text not null,
  mail_from     text not null,
  subject       text,
  body_text     text,                               -- אחרי המרה מ-html
  body_html     text,
  provider      text,                               -- cloudflare
  raw           jsonb,                              -- ה-payload בלי html (ה-html בעמודה שלו)
  received_at   timestamptz not null default now(),
  forwarded_at  timestamptz,
  forward_error text
);
create index if not exists mail_inbound_phone_received_idx on public.mail_inbound (phone_id, received_at desc);
create index if not exists mail_inbound_received_idx       on public.mail_inbound (received_at);

alter table public.mailboxes    enable row level security;
alter table public.mail_inbound enable row level security;


-- ── rpc_mail_inbound ─────────────────────────────────────────────────────────
-- מחזיר {ok, inserted, id, phone_id, manager_url, forwarded}
--   inserted=false + forwarded=false → כבר נשמר אבל לא הועבר (קריסה באמצע) → להעביר שוב
--   phone_id null                     → אין מיפוי. נשמר, לא מועבר
--   manager_url null                  → לטלפון אין host. נשמר, forward_error=no_host
create or replace function public.rpc_mail_inbound(p jsonb)
returns jsonb
language plpgsql
security definer
set search_path = public
as $$
declare
  v_to     text := lower(trim(coalesce(p->>'to', '')));
  v_base   text := regexp_replace(v_to, '\+[^@]*@', '@');     -- otp+tag@d → otp@d
  v_tag    text := substring(v_to from '\+([^@]+)@');
  v_msg    text := nullif(trim(coalesce(p->>'msg_id', '')), '');
  v_phone  uuid;
  v_id     uuid;
  v_fwd    timestamptz;
  v_ip     text;
  v_new    boolean;
begin
  if v_msg is null or v_to = '' then
    return jsonb_build_object('ok', false, 'error', 'msg_id and to are required');
  end if;

  -- +tag שהוא phone id קיים גובר: otp+<A>@ הולך ל-A גם כש-otp@ ממופה ל-B
  if v_tag ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' then
    select id into v_phone from public.phones where id = v_tag::uuid;
  end if;

  if v_phone is null then
    select phone_id into v_phone from public.mailboxes
     where address = v_base and is_active
     limit 1;
  end if;

  insert into public.mail_inbound (msg_id, phone_id, mail_to, mail_from, subject,
                                   body_text, body_html, provider, raw, received_at)
  values (v_msg, v_phone, v_to, lower(trim(coalesce(p->>'from', ''))), p->>'subject',
          p->>'text', p->>'html', p->>'provider', p - 'html',
          coalesce((p->>'received_at')::timestamptz, now()))
  on conflict (msg_id) do nothing
  returning id into v_id;

  v_new := v_id is not null;
  if not v_new then
    -- כפילות: מחזירים את מצב השורה הקיימת, כדי שהעברה שנקטעה תושלם
    select id, phone_id, forwarded_at into v_id, v_phone, v_fwd
      from public.mail_inbound where msg_id = v_msg;
  end if;

  if v_phone is not null then
    select h.ip_address into v_ip
      from public.phones ph join public.agent_hosts h on h.id = ph.host_id
     where ph.id = v_phone;
  end if;

  return jsonb_build_object(
    'ok',          true,
    'inserted',    v_new,
    'id',          v_id,
    'phone_id',    v_phone,
    -- כמו _is_valid_agent_ip ב-backend: host על loopback = אין host
    'manager_url', case when coalesce(trim(v_ip), '') = ''
                          or trim(v_ip) ~* '^(127\.|0\.0\.0\.0$|localhost$|::1$|\[::1\]$)'
                        then null
                        else 'http://' || trim(v_ip) || ':5000' end,
    'forwarded',   v_fwd is not null
  );
end $$;


-- ── rpc_mail_forwarded ───────────────────────────────────────────────────────
create or replace function public.rpc_mail_forwarded(p_id uuid, p_error text default null)
returns void
language sql
security definer
set search_path = public
as $$
  update public.mail_inbound
     set forwarded_at  = case when p_error is null then now() else forwarded_at end,
         forward_error = p_error
   where id = p_id;
$$;


-- ── retention ────────────────────────────────────────────────────────────────
-- הגוף עלול להכיל מידע אישי. ה-relay קורא לזה פעם בשעה (MAIL_RETENTION_DAYS).
create or replace function public.rpc_mail_purge(p_days int default 7)
returns int
language sql
security definer
set search_path = public
as $$
  with d as (
    delete from public.mail_inbound
     where received_at < now() - make_interval(days => greatest(p_days, 1))
    returning 1
  )
  select count(*)::int from d;
$$;


revoke all on function public.rpc_mail_inbound(jsonb)        from public, anon, authenticated;
revoke all on function public.rpc_mail_forwarded(uuid, text) from public, anon, authenticated;
revoke all on function public.rpc_mail_purge(int)            from public, anon, authenticated;
grant execute on function public.rpc_mail_inbound(jsonb)        to service_role;
grant execute on function public.rpc_mail_forwarded(uuid, text) to service_role;
grant execute on function public.rpc_mail_purge(int)            to service_role;


-- ════════════════════════ 12_mailboxes_cloudflare.sql ════════════════════════
-- ════════════════════════════════════════════════════════════════════════════
-- 12_mailboxes_cloudflare.sql — כתובת מייל לכל טלפון: <ספרות>@grossmanbot.download
--
-- mail_provision.py יוצר rule ב-Cloudflare Email Routing (to = הכתובת →
-- Worker mail-worker) ושורה ב-mailboxes. idempotent.
-- ════════════════════════════════════════════════════════════════════════════

alter table public.mailboxes add column if not exists cf_rule_id    text;
alter table public.mailboxes add column if not exists provisioned_at timestamptz;
alter table public.mailboxes add column if not exists last_error    text;

-- טלפון אחד = כתובת פעילה אחת
create unique index if not exists mailboxes_phone_active_uq
  on public.mailboxes (phone_id) where is_active;


-- ════════════════════════ 13_mail_screen.sql ════════════════════════
-- ════════════════════════════════════════════════════════════════════════════
-- 13_mail_screen.sql — טאב "מייל" ב-PhoneDetail, כמו טאב SMS
--
--   rpc_mail_threads  רשימה לפי שולח, מדופדפת + חיפוש (שולח / נושא / גוף)
--   rpc_mail_thread   דיאלוג עם שולח אחד, keyset אחורה (received_at, id)
--
-- הבעלות על הטלפון נבדקת ב-backend לפני הקריאה. service_role בלבד.
-- idempotent.
-- ════════════════════════════════════════════════════════════════════════════

create index if not exists mail_inbound_phone_from_received_idx
  on public.mail_inbound (phone_id, mail_from, received_at desc, id desc);

-- %, _ ו-\ ב-ILIKE כתווים רגילים
create or replace function public._like_escape(p text)
returns text language sql immutable as $$
  select replace(replace(replace(coalesce(p, ''), '\', '\\'), '%', '\%'), '_', '\_')
$$;


-- ── רשימה ────────────────────────────────────────────────────────────────────
-- {items:[{mail_from, count, last_at, last_subject, last_preview}], page, total_pages, total}
-- page מחוץ לטווח → מוצמד לעמוד האחרון (כמו ב-SMS: ה-UI מתעדכן לפי page שחזר)
create or replace function public.rpc_mail_threads(
  p_phone_id  uuid,
  p_search    text default null,
  p_page      int  default 1,
  p_page_size int  default 20
)
returns jsonb
language plpgsql
stable
security definer
set search_path = public
as $$
declare
  v_size  int  := greatest(1, least(coalesce(p_page_size, 20), 100));
  v_q     text := nullif(trim(coalesce(p_search, '')), '');
  v_pat   text := '%' || public._like_escape(v_q) || '%';
  v_total int;
  v_pages int;
  v_page  int;
  v_items jsonb;
begin
  select count(distinct mail_from) into v_total
    from public.mail_inbound m
   where m.phone_id = p_phone_id
     and (v_q is null or m.mail_from ilike v_pat or m.subject ilike v_pat or m.body_text ilike v_pat);

  v_pages := greatest(1, ceil(v_total::numeric / v_size)::int);
  v_page  := greatest(1, least(coalesce(p_page, 1), v_pages));

  select coalesce(jsonb_agg(jsonb_build_object(
           'mail_from',    t.mail_from,
           'count',        t.cnt,
           'last_at',      t.last_at,
           'last_subject', l.subject,
           'last_preview', left(regexp_replace(coalesce(l.body_text, ''), '\s+', ' ', 'g'), 160)
         ) order by t.last_at desc), '[]'::jsonb)
    into v_items
    from (
      select m.mail_from, count(*) as cnt, max(m.received_at) as last_at
        from public.mail_inbound m
       where m.phone_id = p_phone_id
         and (v_q is null or m.mail_from ilike v_pat or m.subject ilike v_pat or m.body_text ilike v_pat)
       group by m.mail_from
       order by max(m.received_at) desc
       limit v_size offset (v_page - 1) * v_size
    ) t
    cross join lateral (
      select m2.subject, m2.body_text
        from public.mail_inbound m2
       where m2.phone_id = p_phone_id and m2.mail_from = t.mail_from
       order by m2.received_at desc, m2.id desc
       limit 1
    ) l;

  return jsonb_build_object('items', v_items, 'page', v_page, 'total_pages', v_pages, 'total', v_total);
end $$;


-- ── דיאלוג ───────────────────────────────────────────────────────────────────
-- {items:[{id, subject, body, sort_at, forwarded, forward_error}] (חדש → ישן), has_more}
-- before_at + before_id = ההודעה הישנה ביותר שכבר מוצגת (keyset, בלי כפילויות)
create or replace function public.rpc_mail_thread(
  p_phone_id  uuid,
  p_from      text,
  p_before_at timestamptz default null,
  p_before_id uuid        default null,
  p_limit     int         default 30
)
returns jsonb
language plpgsql
stable
security definer
set search_path = public
as $$
declare
  v_limit int := greatest(1, least(coalesce(p_limit, 30), 100));
  v_rows  jsonb;
  v_more  boolean;
begin
  select coalesce(jsonb_agg(jsonb_build_object(
           'id',            p.id,
           'subject',       p.subject,
           'body',          left(coalesce(p.body_text, ''), 20000),
           'sort_at',       p.received_at,
           'forwarded',     p.forwarded_at is not null,
           'forward_error', p.forward_error
         ) order by p.received_at desc, p.id desc) filter (where p.rn <= v_limit), '[]'::jsonb),
         count(*) > v_limit
    into v_rows, v_more
    from (
      select m.id, m.subject, m.body_text, m.received_at, m.forwarded_at, m.forward_error,
             row_number() over (order by m.received_at desc, m.id desc) as rn
        from public.mail_inbound m
       where m.phone_id = p_phone_id
         and m.mail_from = lower(trim(coalesce(p_from, '')))
         and (p_before_at is null
              or (m.received_at, m.id) < (p_before_at, coalesce(p_before_id, 'ffffffff-ffff-ffff-ffff-ffffffffffff'::uuid)))
       order by m.received_at desc, m.id desc
       limit v_limit + 1
    ) p;

  return jsonb_build_object('items', v_rows, 'has_more', v_more);
end $$;


revoke all on function public.rpc_mail_threads(uuid, text, int, int)                  from public, anon, authenticated;
revoke all on function public.rpc_mail_thread(uuid, text, timestamptz, uuid, int)     from public, anon, authenticated;
grant execute on function public.rpc_mail_threads(uuid, text, int, int)              to service_role;
grant execute on function public.rpc_mail_thread(uuid, text, timestamptz, uuid, int) to service_role;


-- ════════════════════════ 14_mail_rent_release.sql ════════════════════════
-- 14_mail_rent_release.sql
-- When a phone's owner changes (rental ended → user_id = system, or a new rental), the
-- old owner's mails must not stay on it. rpc_mail_release_stale() finds every mailbox whose
-- user_id differs from the phone's current user_id, deletes that phone's mail_inbound rows
-- and moves the mailbox to the new owner (the mailbox row and the Cloudflare rule stay).
-- Fallback only: trigger trg_mail_phone_owner (15) already does this at the moment of the change.
-- MailboxProvisioner calls it at the start of each sweep.
-- Safe with more than one caller: rows are claimed with FOR UPDATE SKIP LOCKED.

create or replace function public.rpc_mail_release_stale()
returns jsonb
language plpgsql
security definer
set search_path = public
as $$
declare
  r     record;
  n     int;
  out_a jsonb := '[]'::jsonb;
begin
  for r in
    select m.id, m.address, m.phone_id, m.user_id as old_user, p.user_id as new_user
    from public.mailboxes m
    join public.phones p on p.id = m.phone_id
    where p.user_id is distinct from m.user_id
    for update of m skip locked
  loop
    delete from public.mail_inbound where phone_id = r.phone_id;
    get diagnostics n = row_count;
    update public.mailboxes set user_id = r.new_user where id = r.id;

    out_a := out_a || jsonb_build_object(
      'address',  r.address,
      'phone_id', r.phone_id,
      'old_user', r.old_user,
      'new_user', r.new_user,
      'mails',    n);
  end loop;

  return jsonb_build_object('released', out_a);
end;
$$;

revoke all on function public.rpc_mail_release_stale() from public, anon, authenticated;
grant execute on function public.rpc_mail_release_stale() to service_role;


-- ════════════════════════ 15_mail_owner_trigger.sql ════════════════════════
-- 15_mail_owner_trigger.sql
-- The moment phones.user_id changes (rental ended → system, or a new rental), in the same
-- transaction as the change:
--   1. every mail_inbound row of that phone is deleted → the next renter starts empty
--   2. the mailbox stays and is moved to the new owner → no window where mails don't map
-- Works whoever changes the owner (rpc_rent_sweep, RentDevice.py, the backend, a manual update).
-- rpc_mail_release_stale (14) stays as a fallback for anything that got past the trigger.

create or replace function public._mail_on_phone_owner_change()
returns trigger
language plpgsql
security definer
set search_path = public
as $$
begin
  delete from public.mail_inbound where phone_id = new.id;
  update public.mailboxes set user_id = new.user_id where phone_id = new.id;
  return new;
end;
$$;

drop trigger if exists trg_mail_phone_owner on public.phones;
create trigger trg_mail_phone_owner
  after update of user_id on public.phones
  for each row
  when (old.user_id is distinct from new.user_id)
  execute function public._mail_on_phone_owner_change();

revoke all on function public._mail_on_phone_owner_change() from public, anon, authenticated;
