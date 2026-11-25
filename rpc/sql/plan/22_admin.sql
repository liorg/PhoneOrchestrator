-- sql/22_admin.sql — PhoneOrchestrator back office: plans, customers & debts, bot_config
-- Run after 20_plans.sql and 21_billing.sql.
--
-- Every admin RPC is SECURITY DEFINER (reads auth.users) and callable by service_role ONLY:
-- the app's browser holds the anon key, so anything left executable by anon/authenticated
-- could be called straight from DevTools. Same for the plan/billing tables: RLS on, no policies
-- → only service_role (botbackend, PhoneOrchestrator) reads or writes them.

-- ═════════════════════════════════════════════════════ hardening ═══
alter table plans              enable row level security;
alter table user_plans         enable row level security;
alter table user_plan_history  enable row level security;
alter table contact_releases   enable row level security;
alter table billing_charges    enable row level security;
alter table billing_payments   enable row level security;
alter table billing_methods    enable row level security;   -- CardCom token lives here
alter table rent_options       enable row level security;

-- ══════════════════════════════════════════════════ change history ═══
-- Every insert/update of plans and rent_options, with old/new row and who did it.
-- actor: the admin RPCs set app.actor (PhoneOrchestrator passes the caller's IP);
-- a change made anywhere else is recorded as the DB role (e.g. postgres from the SQL editor).
create table if not exists catalog_history (
  id          bigserial primary key,
  kind        text not null check (kind in ('plan', 'rent')),
  ref         text not null,                 -- plan id | rent hours
  op          text not null,                 -- INSERT | UPDATE | DELETE
  old_row     jsonb,
  new_row     jsonb,
  changed     text[],                        -- columns that changed (UPDATE)
  actor       text,
  changed_at  timestamptz not null default now()
);
create index if not exists catalog_history_kind_at on catalog_history (kind, changed_at desc);
alter table catalog_history enable row level security;

create or replace function catalog_history_log() returns trigger
language plpgsql security definer set search_path = public as $$
declare
  v_old jsonb := case when tg_op <> 'INSERT' then to_jsonb(old) end;
  v_new jsonb := case when tg_op <> 'DELETE' then to_jsonb(new) end;
  v_changed text[];
begin
  if tg_op = 'UPDATE' then
    select array_agg(k order by k) into v_changed
      from jsonb_object_keys(v_new) k
     where k <> 'updated_at' and v_new->k is distinct from v_old->k;
    if v_changed is null then return new; end if;        -- nothing really changed
  end if;
  insert into catalog_history (kind, ref, op, old_row, new_row, changed, actor)
  values (tg_argv[0],
          coalesce(v_new->>tg_argv[1], v_old->>tg_argv[1]),
          tg_op, v_old, v_new, v_changed,
          coalesce(nullif(current_setting('app.actor', true), ''), current_user));
  return coalesce(new, old);
end $$;

drop trigger if exists trg_plans_history on plans;
create trigger trg_plans_history after insert or update or delete on plans
  for each row execute function catalog_history_log('plan', 'id');
drop trigger if exists trg_rent_options_history on rent_options;
create trigger trg_rent_options_history after insert or update or delete on rent_options
  for each row execute function catalog_history_log('rent', 'hours');

revoke execute on function rpc_plan_usage(uuid)           from public, anon;
grant  execute on function rpc_plan_usage(uuid)           to authenticated;   -- own user only (guard inside)
revoke execute on function rpc_billing_accrue(uuid)       from public, anon, authenticated;
revoke execute on function rpc_billing_summary(uuid, int) from public, anon, authenticated;
grant  execute on function rpc_plan_usage(uuid)           to service_role;
grant  execute on function rpc_billing_accrue(uuid)       to service_role;
grant  execute on function rpc_billing_summary(uuid, int) to service_role;

-- ═══════════════════════════════════════════════ bot_config help ═══
insert into bot_config (key, value, description) values
  ('admin.page_size', '25', 'Rows per page in the PhoneOrchestrator back office (plans, customers, config). 5–200.')
on conflict (key) do nothing;

-- descriptions for the keys we know — only where none is written yet
update bot_config c set description = d.txt, updated_at = now()
  from (values
    ('plans.default_plan',          'Plan id a user gets before choosing one (free / basic / advanced). A paid plan here means every new user starts owing its monthly price.'),
    ('plans.bot_cooldown_hours',    'Hours a removed bot (agent) keeps its slot blocked. Stops a customer from rotating bots to message many numbers. 0 = no cooldown.'),
    ('admin.page_size',             'Rows per page in the PhoneOrchestrator back office (plans, customers, config). 5–200.'),
    ('rent.warn_percent',           'Percent of the rental time after which the customer gets the "rental ending" notification and the battery icon turns red.'),
    ('schedules_page_size',         'Rows per page in the app''s schedules grid.'),
    ('schedule_log_page_size',      'Rows per page in a schedule''s event log (1–200).'),
    ('mail.paging',                 'Senders per page on the phone''s Mail tab (1–100).')
  ) as d(k, txt)
 where c.key = d.k and coalesce(c.description, '') = '';

create or replace function _admin_page_size() returns int
language sql stable as $$
  select greatest(5, least(200, coalesce(
    (select nullif(trim(both '"' from value), '')::int from bot_config where key = 'admin.page_size' limit 1), 25)));
$$;

-- signatures changed (p_actor added) — drop the old ones if an earlier run created them
drop function if exists rpc_admin_plan_save(jsonb);
drop function if exists rpc_admin_set_plan(uuid, text);

-- ═════════════════════════════════════════════════════════ plans ═══
-- Lists come back as { items: [...] }: SupabaseRpc.Unwrap (PhoneOrchestrator) takes the first
-- element of a top-level array, so a bare array would arrive as one plan.
create or replace function rpc_admin_plans() returns jsonb
language plpgsql stable security definer set search_path = public as $$
declare v_default text := coalesce((select trim(both '"' from value) from bot_config where key = 'plans.default_plan' limit 1), 'free');
begin
  return jsonb_build_object('items', coalesce((
    select jsonb_agg(to_jsonb(p) || jsonb_build_object(
             'customers', (select count(*) from user_plans u where u.plan_id = p.id),
             'is_default', p.id = v_default)
           order by p.sort, p.id)
      from plans p), '[]'::jsonb));
end $$;

-- p: { id, price, phones, bots, schedules, api, load_test, monitoring, cta, highlight, sort, is_active }
-- JSON null in a limit = unlimited.
create or replace function rpc_admin_plan_save(p jsonb, p_actor text default null) returns jsonb
language plpgsql security definer set search_path = public as $$
declare v_id text := lower(trim(p->>'id'));
begin
  perform set_config('app.actor', coalesce(p_actor, 'admin'), true);   -- → catalog_history.actor
  if v_id is null or v_id !~ '^[a-z][a-z0-9_]{1,30}$' then
    raise exception 'plan id: a-z, 0-9, _ (2-31 chars, starts with a letter)';
  end if;
  insert into plans (id, price, phones, bots, schedules, api, load_test, monitoring, cta, highlight, sort, is_active)
  values (v_id,
          (p->>'price')::numeric, (p->>'phones')::int, (p->>'bots')::int, (p->>'schedules')::int,
          coalesce((p->>'api')::boolean, false), coalesce((p->>'load_test')::boolean, false),
          coalesce((p->>'monitoring')::boolean, false),
          coalesce(nullif(p->>'cta', ''), 'signup'), coalesce((p->>'highlight')::boolean, false),
          coalesce((p->>'sort')::int, 0), coalesce((p->>'is_active')::boolean, true))
  on conflict (id) do update set
    price = excluded.price, phones = excluded.phones, bots = excluded.bots, schedules = excluded.schedules,
    api = excluded.api, load_test = excluded.load_test, monitoring = excluded.monitoring,
    cta = excluded.cta, highlight = excluded.highlight, sort = excluded.sort, is_active = excluded.is_active;
  return (select to_jsonb(x) from plans x where x.id = v_id);
end $$;

-- ═══════════════════════════════════════════ customers & debts ═══
-- balance = charges − paid payments, from what is already accrued.
-- "Recalculate charges" (rpc_billing_accrue_all) brings every customer up to date first.
create or replace function rpc_admin_customers(
  p_page int default 1, p_search text default null, p_plan text default null, p_debt_only boolean default false
) returns jsonb
language plpgsql security definer set search_path = public, auth as $$   -- volatile: temp table
declare
  v_size    int  := _admin_page_size();
  v_page    int  := greatest(1, coalesce(p_page, 1));
  v_default text := coalesce((select trim(both '"' from value) from bot_config where key = 'plans.default_plan' limit 1), 'free');
  v_q       text := nullif(trim(coalesce(p_search, '')), '');
  v_total   int;
  v_items   jsonb;
begin
  drop table if exists _c;
  create temp table _c on commit drop as
  select au.id                                                         as user_id,
         au.email::text                                                as email,
         coalesce(au.raw_user_meta_data->>'full_name', au.raw_user_meta_data->>'name') as name,
         au.created_at                                                 as joined_at,
         coalesce(up.plan_id, v_default)                               as plan_id,
         coalesce((select sum(amount) from billing_charges c  where c.user_id = au.id), 0)
       - coalesce((select sum(amount) from billing_payments b where b.user_id = au.id and b.status = 'paid'), 0) as balance,
         (select max(paid_at) from billing_payments b where b.user_id = au.id and b.status = 'paid') as last_paid_at
    from auth.users au
    left join user_plans up on up.user_id = au.id
   where (v_q is null
          or au.email ilike '%' || v_q || '%'
          or (au.raw_user_meta_data->>'full_name') ilike '%' || v_q || '%')
     and (p_plan is null or p_plan = '' or coalesce(up.plan_id, v_default) = p_plan);

  if coalesce(p_debt_only, false) then
    delete from _c where balance <= 0;
  end if;

  select count(*) into v_total from _c;

  select coalesce(jsonb_agg(row_to_json(r) order by r.balance desc, r.joined_at desc), '[]'::jsonb) into v_items
    from (
      select c.*,
             (select count(*) from phones ph where ph.user_id = c.user_id and ph.sim_id is null)  as phones,
             (select count(*) from contacts ct join phones ph on ph.id = ct.phone_id
               where ph.user_id = c.user_id and ct.tag = 'active')                                as bots,
             (select count(*) from schedules s join phones ph on ph.id = s.phone_id
               where ph.user_id = c.user_id and s.status is distinct from 'completed')            as schedules
        from _c c
       order by c.balance desc, c.joined_at desc
       limit v_size offset (v_page - 1) * v_size
    ) r;

  return jsonb_build_object(
    'items', v_items, 'total', v_total, 'page', v_page, 'page_size', v_size,
    'pages', greatest(1, ceil(v_total::numeric / v_size)::int),
    'debt_total', (select coalesce(sum(balance), 0) from _c where balance > 0));
end $$;

-- one customer: brings charges up to date, then plan + usage + months + payments
create or replace function rpc_admin_customer(p_user_id uuid) returns jsonb
language plpgsql security definer set search_path = public, auth as $$
declare v_user jsonb;
begin
  select jsonb_build_object(
           'user_id', au.id, 'email', au.email,
           'name', coalesce(au.raw_user_meta_data->>'full_name', au.raw_user_meta_data->>'name'),
           'joined_at', au.created_at, 'last_sign_in_at', au.last_sign_in_at)
    into v_user
    from auth.users au where au.id = p_user_id;
  if v_user is null then return null; end if;

  perform rpc_billing_accrue(p_user_id);

  return jsonb_build_object(
    'user',    v_user,
    'usage',   rpc_plan_usage(p_user_id),
    'billing', rpc_billing_summary(p_user_id, 24),
    'method',  (select jsonb_build_object('last4', last4, 'exp_month', exp_month, 'exp_year', exp_year, 'brand', brand)
                  from billing_methods where user_id = p_user_id),
    'plan_history', coalesce((
       select jsonb_agg(jsonb_build_object('plan_id', h.plan_id, 'from_at', h.from_at, 'source', h.source,
                                           'price', p.price) order by h.from_at desc, h.id desc)
         from (select * from user_plan_history where user_id = p_user_id order by from_at desc, id desc limit 50) h
         left join plans p on p.id = h.plan_id), '[]'::jsonb),
    'rentals', coalesce((
       select jsonb_agg(jsonb_build_object('id', r.id, 'number', ph.number, 'start_time', r.start_time,
                                           'end_time', r.end_time, 'ended_at', r.ended_at, 'end_reason', r.end_reason,
                                           'status', r.status,
                                           'amount', (select amount from billing_charges c
                                                       where c.user_id = p_user_id and c.kind = 'rent' and c.ref = 'rent:' || r.id))
                         order by r.start_time desc)
         from (select * from rent where user_id = p_user_id order by start_time desc limit 50) r
         left join phones ph on ph.id = r.phone_id), '[]'::jsonb));
end $$;

create or replace function rpc_admin_set_plan(p_user_id uuid, p_plan_id text, p_actor text default null) returns jsonb
language plpgsql security definer set search_path = public as $$
begin
  if not exists (select 1 from plans where id = p_plan_id) then
    raise exception 'unknown plan %', p_plan_id;
  end if;
  insert into user_plans (user_id, plan_id, source, changed_at) values (p_user_id, p_plan_id, 'admin', now())
  on conflict (user_id) do update set plan_id = excluded.plan_id, source = 'admin', changed_at = now();
  insert into user_plan_history (user_id, plan_id, from_at, source)
  values (p_user_id, p_plan_id, now(), 'admin' || coalesce(' · ' || p_actor, ''));
  return rpc_plan_usage(p_user_id);
end $$;

-- bank transfer / cash / write-off — anything not through CardCom
create or replace function rpc_admin_add_payment(p_user_id uuid, p_amount numeric, p_note text default null)
returns jsonb
language plpgsql security definer set search_path = public as $$
declare v_row billing_payments%rowtype;
begin
  if p_amount is null or p_amount = 0 then raise exception 'amount must not be 0'; end if;
  insert into billing_payments (user_id, amount, provider, status, paid_at, raw)
  values (p_user_id, round(p_amount, 2), 'manual', 'paid', now(), jsonb_build_object('note', p_note))
  returning * into v_row;
  return to_jsonb(v_row);
end $$;

-- every customer that has a plan history or a rental
create or replace function rpc_billing_accrue_all() returns jsonb
language plpgsql security definer set search_path = public as $$
declare v_id uuid; v_n int := 0;
begin
  for v_id in select user_id from user_plan_history union select user_id from rent where user_id is not null loop
    perform rpc_billing_accrue(v_id);
    v_n := v_n + 1;
  end loop;
  return jsonb_build_object('customers', v_n, 'at', now());
end $$;

-- ═════════════════════════════════════════════════════ bot_config ═══
create or replace function rpc_admin_config_list(p_page int default 1, p_search text default null) returns jsonb
language plpgsql stable security definer set search_path = public as $$
declare
  v_size  int := _admin_page_size();
  v_page  int := greatest(1, coalesce(p_page, 1));
  v_q     text := nullif(trim(coalesce(p_search, '')), '');
  v_total int;
begin
  select count(*) into v_total from bot_config
   where v_q is null or key ilike '%' || v_q || '%' or description ilike '%' || v_q || '%';

  return jsonb_build_object(
    'items', coalesce((
      select jsonb_agg(jsonb_build_object('key', key, 'value', value, 'description', description, 'updated_at', updated_at)
                       order by key)
        from (select * from bot_config
               where v_q is null or key ilike '%' || v_q || '%' or description ilike '%' || v_q || '%'
               order by key limit v_size offset (v_page - 1) * v_size) x), '[]'::jsonb),
    'total', v_total, 'page', v_page, 'page_size', v_size,
    'pages', greatest(1, ceil(v_total::numeric / v_size)::int));
end $$;

-- p_description null = keep the existing one
create or replace function rpc_admin_config_set(p_key text, p_value text, p_description text default null)
returns jsonb
language plpgsql security definer set search_path = public as $$
declare v_key text := trim(p_key);
begin
  if v_key is null or v_key = '' then raise exception 'key is required'; end if;
  insert into bot_config (key, value, description, updated_at) values (v_key, p_value, p_description, now())
  on conflict (key) do update set
    value = excluded.value,
    description = coalesce(excluded.description, bot_config.description),
    updated_at = now();
  return (select jsonb_build_object('key', key, 'value', value, 'description', description, 'updated_at', updated_at)
            from bot_config where key = v_key);
end $$;

-- ═════════════════════════════════════════════════════════ rentals ═══
create or replace function rpc_admin_rent_options() returns jsonb
language sql stable security definer set search_path = public as $$
  select jsonb_build_object('items', coalesce(jsonb_agg(jsonb_build_object(
           'hours', o.hours, 'price', o.price, 'is_active', o.is_active, 'sort', o.sort, 'updated_at', o.updated_at,
           'per_hour', round(o.price / o.hours, 2),
           'rentals', (select count(*) from rent r
                        where ceil(extract(epoch from (r.end_time - r.start_time)) / 3600.0)::int = o.hours))
         order by o.sort, o.hours), '[]'::jsonb))
    from rent_options o;
$$;

-- p: { hours, price, is_active, sort }. A length is never deleted (rentals reference it): turn it off.
create or replace function rpc_admin_rent_option_save(p jsonb, p_actor text default null) returns jsonb
language plpgsql security definer set search_path = public as $$
declare v_h int := (p->>'hours')::int;
begin
  perform set_config('app.actor', coalesce(p_actor, 'admin'), true);
  if v_h is null or v_h <= 0 or v_h > 24 * 31 then raise exception 'hours: 1–744'; end if;
  if (p->>'price') is null or (p->>'price')::numeric < 0 then raise exception 'price must be 0 or more'; end if;
  insert into rent_options (hours, price, is_active, sort, updated_at)
  values (v_h, round((p->>'price')::numeric, 2), coalesce((p->>'is_active')::boolean, true),
          coalesce((p->>'sort')::int, 0), now())
  on conflict (hours) do update set
    price = excluded.price, is_active = excluded.is_active, sort = excluded.sort, updated_at = now();
  return (select to_jsonb(o) from rent_options o where o.hours = v_h);
end $$;

-- rentals, newest first. p_status: active (end_time in the future, not ended) | ended | null = all
create or replace function rpc_admin_rentals(p_page int default 1, p_status text default null, p_search text default null)
returns jsonb
language plpgsql stable security definer set search_path = public, auth as $$
declare
  v_size  int := _admin_page_size();
  v_page  int := greatest(1, coalesce(p_page, 1));
  v_q     text := nullif(trim(coalesce(p_search, '')), '');
  v_total int;
  v_items jsonb;
begin
  with base as (
    select r.*, ph.number, au.email::text as email,
           (r.ended_at is null and r.end_time > now()) as is_active
      from rent r
      left join phones ph on ph.id = r.phone_id
      left join auth.users au on au.id = r.user_id
     where (v_q is null or ph.number ilike '%' || v_q || '%' or au.email ilike '%' || v_q || '%')
  ), f as (
    select * from base
     where p_status is null or p_status = ''
        or (p_status = 'active' and is_active)
        or (p_status = 'ended'  and not is_active)
  )
  select (select count(*) from f),
         coalesce((select jsonb_agg(jsonb_build_object(
             'id', x.id, 'user_id', x.user_id, 'email', x.email, 'number', x.number,
             'start_time', x.start_time, 'end_time', x.end_time, 'ended_at', x.ended_at,
             'end_reason', x.end_reason, 'status', x.status, 'is_active', x.is_active,
             'hours', ceil(extract(epoch from (x.end_time - x.start_time)) / 3600.0)::int,
             'amount', (select amount from billing_charges c where c.kind = 'rent' and c.ref = 'rent:' || x.id))
             order by x.start_time desc)
           from (select * from f order by start_time desc limit v_size offset (v_page - 1) * v_size) x), '[]'::jsonb)
    into v_total, v_items;

  return jsonb_build_object('items', v_items, 'total', v_total, 'page', v_page, 'page_size', v_size,
                            'pages', greatest(1, ceil(v_total::numeric / v_size)::int));
end $$;

-- ═══════════════════════════════════════════════════ change history ═══
-- p_kind: plan | rent. p_ref: one plan id / one length, or null for all.
create or replace function rpc_admin_history(p_kind text, p_page int default 1, p_ref text default null) returns jsonb
language plpgsql stable security definer set search_path = public as $$
declare
  v_size  int := _admin_page_size();
  v_page  int := greatest(1, coalesce(p_page, 1));
  v_total int;
begin
  select count(*) into v_total from catalog_history
   where kind = p_kind and (p_ref is null or p_ref = '' or ref = p_ref);
  return jsonb_build_object(
    'items', coalesce((
      select jsonb_agg(jsonb_build_object('id', id, 'ref', ref, 'op', op, 'changed', changed,
                                          'old', old_row, 'new', new_row, 'actor', actor, 'at', changed_at)
                       order by changed_at desc, id desc)
        from (select * from catalog_history
               where kind = p_kind and (p_ref is null or p_ref = '' or ref = p_ref)
               order by changed_at desc, id desc limit v_size offset (v_page - 1) * v_size) h), '[]'::jsonb),
    'total', v_total, 'page', v_page, 'page_size', v_size,
    'pages', greatest(1, ceil(v_total::numeric / v_size)::int));
end $$;

-- ═════════════════════════════════════════════════ service_role only ═══
do $$
declare f text;
begin
  foreach f in array array[
    '_admin_page_size()', 'rpc_admin_plans()', 'rpc_admin_plan_save(jsonb, text)',
    'rpc_admin_customers(int, text, text, boolean)', 'rpc_admin_customer(uuid)',
    'rpc_admin_set_plan(uuid, text, text)', 'rpc_admin_add_payment(uuid, numeric, text)',
    'rpc_admin_rent_options()', 'rpc_admin_rent_option_save(jsonb, text)',
    'rpc_admin_rentals(int, text, text)', 'rpc_admin_history(text, int, text)', 'catalog_history_log()',
    'rpc_billing_accrue_all()', 'rpc_admin_config_list(int, text)', 'rpc_admin_config_set(text, text, text)'
  ] loop
    execute format('revoke execute on function %s from public, anon, authenticated', f);
    execute format('grant execute on function %s to service_role', f);
  end loop;
end $$;