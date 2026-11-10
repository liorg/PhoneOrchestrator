-- ════════════════════════════════════════════════════════════════════════════
-- 04_rent_rpc.sql — השכרה / סיום / פקיעה / רשימה. נקרא מ-RentDevice.py (service role)
-- ════════════════════════════════════════════════════════════════════════════

-- השכרה: רק טלפון עם sim_id, של system, בלי השכרה פעילה. ניקוי → user_id → שורה ב-rent
-- משך: p_hours, ואם null — bot_config 'rent.hours' (שעות, מותר שבר: 0.05 = 3 דק' לפיתוח)
drop function if exists rpc_rent_start(uuid, uuid, int, text[]);

create or replace function rpc_rent_start(
  p_phone_id uuid, p_user_id uuid, p_hours numeric default null, p_keep_templates text[] default '{}')
returns jsonb
language plpgsql
as $$
declare
  v_sys   uuid := _rent_system_user();
  v_ph    phones;
  v_rent  rent;
  v_wipe  jsonb;
  v_hours numeric;
begin
  if p_user_id is null or p_user_id = v_sys then
    return jsonb_build_object('ok', false, 'reason', 'bad_user');
  end if;

  select * into v_ph from phones where id = p_phone_id for update;
  if v_ph.id is null then
    return jsonb_build_object('ok', false, 'reason', 'not_found');
  end if;
  if v_ph.sim_id is null then
    return jsonb_build_object('ok', false, 'reason', 'not_rentable');
  end if;
  if v_ph.user_id is distinct from v_sys
     or exists (select 1 from rent where phone_id = p_phone_id and status = 'rent') then
    return jsonb_build_object('ok', false, 'reason', 'not_available');
  end if;

  v_hours := coalesce(p_hours, (select nullif(value, '')::numeric from bot_config where key = 'rent.hours'), 1);
  v_hours := least(greatest(v_hours, 0.01), 168);

  v_wipe := _rent_wipe_phone(p_phone_id, p_keep_templates);

  update phones set user_id = p_user_id where id = p_phone_id;

  insert into rent (phone_id, user_id, start_time, end_time, status)
  values (p_phone_id, p_user_id, now(), now() + make_interval(secs => round(v_hours * 3600)::int), 'rent')
  returning * into v_rent;

  return jsonb_build_object('ok', true, 'rent', to_jsonb(v_rent), 'wiped', v_wipe);
end;
$$;


-- סיום: p_user_id = null → מערכת (expired/admin), אחרת רק בעל ההשכרה.
-- ניקוי → user_id = system → rent.status = available
create or replace function rpc_rent_end(
  p_phone_id uuid, p_user_id uuid default null, p_reason text default null,
  p_keep_templates text[] default '{}')
returns jsonb
language plpgsql
as $$
declare
  v_sys   uuid := _rent_system_user();
  v_rent  rent;
  v_wipe  jsonb;
begin
  perform 1 from phones where id = p_phone_id for update;

  select * into v_rent from rent where phone_id = p_phone_id and status = 'rent' for update;
  if v_rent.id is null then
    return jsonb_build_object('ok', false, 'reason', 'not_rented');
  end if;
  if p_user_id is not null and v_rent.user_id <> p_user_id then
    return jsonb_build_object('ok', false, 'reason', 'not_owner');
  end if;

  v_wipe := _rent_wipe_phone(p_phone_id, p_keep_templates);

  update phones set user_id = v_sys where id = p_phone_id;

  update rent
     set status     = 'available',
         ended_at   = now(),
         end_reason = coalesce(p_reason, case when p_user_id is null then 'admin' else 'user' end)
   where id = v_rent.id
  returning * into v_rent;

  return jsonb_build_object('ok', true, 'rent', to_jsonb(v_rent), 'wiped', v_wipe);
end;
$$;


-- sweep: כל השכרה ש-end_time שלה עבר. מחזיר את ה-phone_id-ים (לפרסום תבניות מחדש)
create or replace function rpc_rent_expire(p_keep_templates text[] default '{}')
returns jsonb
language plpgsql
as $$
declare
  r     record;
  v_out jsonb := '[]'::jsonb;
  v_res jsonb;
begin
  for r in
    select phone_id from rent
     where status = 'rent' and end_time <= now()
     order by end_time
     for update skip locked
  loop
    v_res := rpc_rent_end(r.phone_id, null, 'expired', p_keep_templates);
    if (v_res->>'ok')::boolean then
      v_out := v_out || to_jsonb(r.phone_id);
    end if;
  end loop;
  return v_out;
end;
$$;


-- רשימה למשתמש: זמינים + שלי. "של אחרים" לא מוצגים
create or replace function rpc_rent_list(
  p_user_id uuid, p_page int default 1, p_page_size int default 10, p_search text default null)
returns jsonb
language plpgsql
as $$
declare
  v_sys    uuid := _rent_system_user();
  v_size   int  := least(greatest(coalesce(p_page_size, 10), 1), 100);
  v_page   int  := greatest(coalesce(p_page, 1), 1);
  v_q      text := nullif(trim(coalesce(p_search, '')), '');
  v_total  int;
  v_pages  int;
  v_items  jsonb;
begin
  select count(*) into v_total
    from phones ph
    left join rent r on r.phone_id = ph.id and r.status = 'rent'
   where ph.sim_id is not null
     and ((ph.user_id = v_sys and r.id is null) or r.user_id = p_user_id)
     and (v_q is null or ph.number ilike '%' || v_q || '%' or ph.label ilike '%' || v_q || '%');

  v_pages := greatest(1, ceil(v_total::numeric / v_size)::int);
  v_page  := least(v_page, v_pages);

  select coalesce(jsonb_agg(t.x order by t.mine desc, t.number), '[]'::jsonb)
    into v_items
  from (
    select (r.id is not null) as mine, ph.number,
           jsonb_build_object(
             'phone_id',       ph.id,
             'number',         ph.number,
             'label',          ph.label,
             'status',         ph.status,
             'docker_status',  ph.docker_status,
             'slot',           s.slot,
             'carrier',        s.carrier,
             'wa_type',        s.wa_type,
             'verified',       s.verified,
             'verified_at',    s.verified_at,
             'linked_name',    s.linked_name,
             'device_status',  d.status,
             'adb_state',      d.adb_state,
             'device_seen_at', d.last_seen_at,
             'mine',           r.id is not null,
             'available',      r.id is null,
             'rent', case when r.id is not null then jsonb_build_object(
                        'id', r.id, 'start_time', r.start_time, 'end_time', r.end_time) end
           ) as x
      from phones ph
      left join rent r   on r.phone_id = ph.id and r.status = 'rent'
      left join sim s    on s.id = ph.sim_id
      left join device d on d.id = s.device_id
     where ph.sim_id is not null
       and ((ph.user_id = v_sys and r.id is null) or r.user_id = p_user_id)
       and (v_q is null or ph.number ilike '%' || v_q || '%' or ph.label ilike '%' || v_q || '%')
     order by (r.id is not null) desc, ph.number
     limit v_size offset (v_page - 1) * v_size
  ) t;

  return jsonb_build_object('items', v_items, 'total', v_total, 'page', v_page,
                            'page_size', v_size, 'total_pages', v_pages);
end;
$$;
