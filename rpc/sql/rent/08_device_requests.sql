-- ════════════════════════════════════════════════════════════════════════════
-- 08_device_requests.sql — device_pull: אימות מחדש (קישור WhatsApp בקוד צימוד)
--
--   create   → pending            (יש פתוחה לאותו sim? pending → לא קורה כלום;
--                                   delete_old/ack → לא מכניס עד שתפוג)
--   docker   → pull כל X שניות, מקבל pending
--   docker   → מוחק את הקישור הקיים → delete_old   (לא היה קישור → ack)
--   docker   → מבקש paircode לפי pull id → provisioner ב-Manager (TODO), timeout 2 דק'
--   docker   → מבצע paircode → success / failed.  success → sim.verified + verified_at + linked_name
--   claim מחזיר sim.linked_name — ה-docker מנתק רק אותו. delete_old/ack → verified=false, linked_name=null
--   delete_old/ack שלא הסתיימו תוך pull.paircode_timeout_sec → expired
--
-- מחליף את device_request (הגרסה הקודמת של הקובץ).
-- ════════════════════════════════════════════════════════════════════════════

drop function if exists rpc_device_request_create(uuid, text, text, text, int);
drop function if exists rpc_device_request_claim(text, text[], int);
drop function if exists rpc_device_request_finish(uuid, boolean, jsonb, text);
drop table if exists device_request;

create table if not exists device_pull (
  id           uuid primary key default gen_random_uuid(),
  sim_id       uuid not null references sim(id) on delete cascade,
  phone_id     uuid references phones(id) on delete set null,
  status       text not null default 'pending'
               check (status in ('pending', 'delete_old', 'ack', 'success', 'failed', 'expired')),
  claimed_by   text,                -- update_from של ה-docker
  claimed_at   timestamptz,         -- lease: pending לא חוזר ל-docker אחר בזמן ה-lease
  acked_at     timestamptz,         -- delete_old / ack
  paircode_at  timestamptz,         -- הבקשה ל-provisioner
  linked_name  text,                -- שם המכשיר שנקשר (success)
  unlinked_name text,               -- שם המכשיר שנותק (delete_old)
  expires_at   timestamptz,         -- acked_at + pull.paircode_timeout_sec
  finished_at  timestamptz,
  result       jsonb,
  error        text,
  created_at   timestamptz not null default now(),
  modified_at  timestamptz not null default now()
);

-- אחת פתוחה לכל sim
alter table device_pull add column if not exists linked_name   text;
alter table device_pull add column if not exists unlinked_name text;

create unique index if not exists device_pull_open_uidx
  on device_pull (sim_id) where status in ('pending', 'delete_old', 'ack');
create index if not exists device_pull_pending_idx on device_pull (created_at) where status = 'pending';
create index if not exists device_pull_phone_idx   on device_pull (phone_id, created_at desc);

alter table device_pull enable row level security;


create or replace function _pull_cfg(p_key text, p_default int)
returns int
language sql
stable
as $$
  select coalesce((select nullif(value, '')::int from bot_config where key = p_key), p_default)
$$;


-- delete_old / ack שעבר להם הזמן → expired
create or replace function _pull_expire()
returns void
language sql
as $$
  update device_pull
     set status = 'expired', finished_at = now(), modified_at = now(),
         error = coalesce(error, 'paircode timeout')
   where status in ('delete_old', 'ack') and expires_at <= now();
$$;


create or replace function rpc_pull_create(p_phone_id uuid)
returns jsonb
language plpgsql
as $$
declare
  v_sim   sim;
  v_open  device_pull;
  v_row   device_pull;
begin
  select s.* into v_sim from sim s join phones ph on ph.sim_id = s.id where ph.id = p_phone_id;
  if v_sim.id is null then
    return jsonb_build_object('ok', false, 'reason', 'not_rentable');
  end if;
  if v_sim.wa_type is null then
    return jsonb_build_object('ok', false, 'reason', 'wa_not_installed');
  end if;

  perform _pull_expire();

  select * into v_open from device_pull
   where sim_id = v_sim.id and status in ('pending', 'delete_old', 'ack');
  if v_open.id is not null then
    if v_open.status = 'pending' then
      -- כבר ממתינה ל-pull — לא קורה כלום
      return jsonb_build_object('ok', true, 'created', false, 'pull', to_jsonb(v_open));
    end if;
    -- delete_old / ack — מחכים שיפוג
    return jsonb_build_object('ok', false, 'created', false, 'reason', 'in_progress',
                              'status', v_open.status, 'expires_at', v_open.expires_at);
  end if;

  insert into device_pull (sim_id, phone_id)
  values (v_sim.id, p_phone_id)
  on conflict (sim_id) where status in ('pending', 'delete_old', 'ack') do nothing
  returning * into v_row;

  if v_row.id is null then   -- נוצרה במקביל
    select * into v_row from device_pull
     where sim_id = v_sim.id and status in ('pending', 'delete_old', 'ack');
    return jsonb_build_object('ok', v_row.status = 'pending', 'created', false, 'pull', to_jsonb(v_row));
  end if;

  return jsonb_build_object('ok', true, 'created', true, 'pull', to_jsonb(v_row));
end;
$$;


-- pull: pending של ה-serials שמחוברים ל-docker. lease של pull.lease_sec —
-- מה שנלקח לא חוזר ל-docker אחר עד שה-lease נגמר (docker שנפל באמצע)
create or replace function rpc_pull_claim(p_update_from text, p_serials text[])
returns jsonb
language plpgsql
as $$
declare
  v_lease int := _pull_cfg('pull.lease_sec', 60);
  v_out   jsonb;
begin
  perform _pull_expire();

  with cand as (
    select p.id
      from device_pull p
      join sim s    on s.id = p.sim_id
      join device d on d.id = s.device_id
     where p.status = 'pending'
       and d.device_id = any (coalesce(p_serials, '{}'))
       and d.status = 'add'
       and (p.claimed_at is null or p.claimed_at < now() - make_interval(secs => v_lease))
     order by p.created_at
     for update of p skip locked
  ),
  claimed as (
    update device_pull p
       set claimed_at = now(), claimed_by = nullif(p_update_from, ''), modified_at = now()
      from cand
     where p.id = cand.id
    returning p.id, p.sim_id, p.phone_id, p.created_at
  )
  select coalesce(jsonb_agg(jsonb_build_object(
           'id', c.id, 'serial', d.device_id, 'slot', s.slot, 'phone_number', s.phone_number,
           'wa_type', s.wa_type, 'linked_name', s.linked_name,
           'pkg', case s.wa_type when 'business' then 'com.whatsapp.w4b' else 'com.whatsapp' end)
         order by c.created_at), '[]'::jsonb)
    into v_out
    from claimed c
    join sim s    on s.id = c.sim_id
    join device d on d.id = s.device_id;

  return v_out;
end;
$$;


-- הקישור הקיים נמחק → delete_old. לא היה קישור → ack. מכאן רץ ה-timeout לקוד הצימוד.
-- בשני המקרים ה-SIM כבר לא מקושר: verified=false, linked_name=null
drop function if exists rpc_pull_ack(uuid, boolean, jsonb);

create or replace function rpc_pull_ack(
  p_id uuid, p_unlinked boolean, p_result jsonb default null, p_unlinked_name text default null)
returns jsonb
language plpgsql
as $$
declare
  v_row device_pull;
begin
  update device_pull
     set status        = case when p_unlinked then 'delete_old' else 'ack' end,
         acked_at      = now(),
         expires_at    = now() + make_interval(secs => _pull_cfg('pull.paircode_timeout_sec', 120)),
         result        = p_result,
         unlinked_name = case when p_unlinked
                              then coalesce(nullif(p_unlinked_name, ''),
                                            (select linked_name from sim where id = device_pull.sim_id))
                         end,
         modified_at   = now()
   where id = p_id and status = 'pending'
  returning * into v_row;

  if v_row.id is null then
    return jsonb_build_object('ok', false, 'reason', 'not_pending');
  end if;

  update sim set verified = false, linked_name = null, modified_at = now() where id = v_row.sim_id;

  return jsonb_build_object('ok', true, 'status', v_row.status, 'expires_at', v_row.expires_at,
                            'unlinked_name', v_row.unlinked_name);
end;
$$;


-- לפני שה-backend פונה ל-provisioner: מוודא delete_old/ack שלא פג, ומחזיר למי לבקש
create or replace function rpc_pull_paircode(p_id uuid)
returns jsonb
language plpgsql
as $$
declare
  v_row device_pull;
  v_sim sim;
begin
  perform _pull_expire();

  update device_pull set paircode_at = now(), modified_at = now()
   where id = p_id and status in ('delete_old', 'ack')
  returning * into v_row;

  if v_row.id is null then
    select * into v_row from device_pull where id = p_id;
    return jsonb_build_object('ok', false,
                              'reason', case when v_row.id is null then 'not_found' else 'status_' || v_row.status end);
  end if;

  select * into v_sim from sim where id = v_row.sim_id;
  return jsonb_build_object(
    'ok', true, 'id', v_row.id, 'phone_id', v_row.phone_id, 'phone_number', v_sim.phone_number,
    'wa_type', v_sim.wa_type,
    'pkg', case v_sim.wa_type when 'business' then 'com.whatsapp.w4b' else 'com.whatsapp' end,
    'expires_at', v_row.expires_at);
end;
$$;


-- תוצאת קוד הצימוד. success → sim.verified = true + verified_at + linked_name.
-- מתקבל גם אם expires_at עבר ממש עכשיו — הקישור עצמו הוא העובדה
drop function if exists rpc_pull_finish(uuid, boolean, text, jsonb);

create or replace function rpc_pull_finish(
  p_id uuid, p_ok boolean, p_error text default null, p_result jsonb default null,
  p_linked_name text default null)
returns jsonb
language plpgsql
as $$
declare
  v_row device_pull;
  v_name text := nullif(trim(coalesce(p_linked_name, '')), '');
begin
  update device_pull
     set status      = case when p_ok then 'success' else 'failed' end,
         error       = case when p_ok then null else left(coalesce(p_error, 'failed'), 1000) end,
         result      = coalesce(p_result, result),
         linked_name = case when p_ok then v_name end,
         finished_at = now(),
         modified_at = now()
   where id = p_id and status in ('delete_old', 'ack')
  returning * into v_row;

  if v_row.id is null then
    return jsonb_build_object('ok', false, 'reason', 'not_in_progress');
  end if;

  if p_ok then
    update sim set verified = true, verified_at = now(), linked_name = v_name, modified_at = now()
     where id = v_row.sim_id;
  end if;

  return jsonb_build_object('ok', true, 'status', v_row.status, 'linked_name', v_row.linked_name);
end;
$$;


do $$
declare f text;
begin
  foreach f in array array[
    'rpc_pull_create(uuid)',
    'rpc_pull_claim(text,text[])',
    'rpc_pull_ack(uuid,boolean,jsonb,text)',
    'rpc_pull_paircode(uuid)',
    'rpc_pull_finish(uuid,boolean,text,jsonb,text)'
  ] loop
    execute format('revoke all on function %s from public, anon, authenticated', f);
    execute format('grant execute on function %s to service_role', f);
  end loop;
  execute 'revoke all on function _pull_expire() from public, anon, authenticated';
  execute 'revoke all on function _pull_cfg(text,int) from public, anon, authenticated';
  execute 'grant execute on function _pull_expire() to service_role';
  execute 'grant execute on function _pull_cfg(text,int) to service_role';
end $$;

insert into bot_config (key, value, description) values
  ('pull.paircode_timeout_sec', '120', 'device_pull: seconds from delete_old/ack until expired'),
  ('pull.lease_sec',            '60',  'device_pull: seconds a claimed pending is hidden from other pulls')
on conflict (key) do nothing;
