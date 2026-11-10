-- ════════════════════════════════════════════════════════════════════════════
-- 09_orch_devices.sql — מסך הניהול (PhoneOrchestrator): מכשירים → drilldown SIM
--   rpc_orch_list_devices      רשימה מדופדפת, כמו rpc_orch_list_hosts
--   rpc_orch_list_device_sims  ה-SIM-ים של מכשיר: מספר, WhatsApp, אימות, לקוח, pull
--   rpc_orch_release_sim       ניתוק מהלקוח: ניקוי → system. עם השכרה פעילה או בלי
-- אימות מחדש = rpc_pull_create (08)
-- ════════════════════════════════════════════════════════════════════════════

create or replace function rpc_orch_list_devices(p_page int default 1, p_page_size int default null)
returns jsonb
language plpgsql
as $$
declare
  v_size  int := least(greatest(coalesce(p_page_size,
                   (select nullif(value, '')::int from bot_config where key = 'orch.devices.paging'), 20), 1), 100);
  v_page  int := greatest(coalesce(p_page, 1), 1);
  v_sys   uuid := _rent_system_user();
  v_total int;
  v_pages int;
  v_items jsonb;
begin
  select count(*) into v_total from device;
  v_pages := greatest(1, ceil(v_total::numeric / v_size)::int);
  v_page  := least(v_page, v_pages);

  select coalesce(jsonb_agg(t.x order by t.device_id), '[]'::jsonb) into v_items
  from (
    select d.device_id,
           jsonb_build_object(
             'id', d.id, 'device_id', d.device_id, 'status', d.status, 'adb_state', d.adb_state,
             'update_from', d.update_from, 'model', d.model, 'android', d.android,
             'last_seen_at', d.last_seen_at, 'modified_at', d.modified_at,
             'sims',      (select count(*) from sim s where s.device_id = d.id and s.status = 'active'),
             'verified',  (select count(*) from sim s where s.device_id = d.id and s.status = 'active' and s.verified),
             'rented',    (select count(*) from sim s join phones ph on ph.sim_id = s.id
                            where s.device_id = d.id and ph.user_id is distinct from v_sys),
             'open_pulls', (select count(*) from device_pull p join sim s on s.id = p.sim_id
                            where s.device_id = d.id and p.status in ('pending', 'delete_old', 'ack'))
           ) as x
      from device d
     order by d.device_id
     limit v_size offset (v_page - 1) * v_size
  ) t;

  return jsonb_build_object('items', v_items, 'total', v_total, 'page', v_page,
                            'page_size', v_size, 'pages', v_pages);
end;
$$;


create or replace function rpc_orch_list_device_sims(p_device_id uuid)
returns jsonb
language plpgsql
as $$
declare
  v_sys uuid := _rent_system_user();
begin
  return coalesce((
    select jsonb_agg(jsonb_build_object(
             'id', s.id, 'slot', s.slot, 'phone_number', s.phone_number, 'carrier', s.carrier,
             'status', s.status, 'wa_type', s.wa_type, 'verified', s.verified,
             'verified_at', s.verified_at, 'linked_name', s.linked_name,
             'phone_id', ph.id,
             'owner', case when ph.id is null then null else jsonb_build_object(
                        'user_id', ph.user_id, 'email', u.email,
                        'is_system', ph.user_id = v_sys) end,
             'rent', (select jsonb_build_object('id', r.id, 'start_time', r.start_time, 'end_time', r.end_time)
                        from rent r where r.phone_id = ph.id and r.status = 'rent'),
             'pull', (select jsonb_build_object('id', p.id, 'status', p.status, 'created_at', p.created_at,
                                                'expires_at', p.expires_at, 'error', p.error,
                                                'finished_at', p.finished_at)
                        from device_pull p where p.sim_id = s.id
                       order by p.created_at desc limit 1)
           ) order by s.slot)
      from sim s
      left join phones ph on ph.sim_id = s.id
      left join users u   on u.id = ph.user_id
     where s.device_id = p_device_id
  ), '[]'::jsonb);
end;
$$;


-- ניתוק מהלקוח: השכרה פעילה → rpc_rent_end (admin). טלפון של לקוח בלי השכרה → ניקוי + system
create or replace function rpc_orch_release_sim(p_sim_id uuid, p_keep_templates text[] default '{}')
returns jsonb
language plpgsql
as $$
declare
  v_sys   uuid := _rent_system_user();
  v_ph    phones;
  v_wipe  jsonb;
begin
  select * into v_ph from phones where sim_id = p_sim_id for update;
  if v_ph.id is null then
    return jsonb_build_object('ok', false, 'reason', 'no_phone');
  end if;

  if exists (select 1 from rent where phone_id = v_ph.id and status = 'rent') then
    return rpc_rent_end(v_ph.id, null, 'admin', p_keep_templates);
  end if;

  if v_ph.user_id = v_sys then
    return jsonb_build_object('ok', false, 'reason', 'already_system');
  end if;

  v_wipe := _rent_wipe_phone(v_ph.id, p_keep_templates);
  update phones set user_id = v_sys where id = v_ph.id;
  return jsonb_build_object('ok', true, 'wiped', v_wipe);
end;
$$;


do $$
declare f text;
begin
  foreach f in array array[
    'rpc_orch_list_devices(int,int)',
    'rpc_orch_list_device_sims(uuid)',
    'rpc_orch_release_sim(uuid,text[])'
  ] loop
    execute format('revoke all on function %s from public, anon, authenticated', f);
    execute format('grant execute on function %s to service_role', f);
  end loop;
end $$;

insert into bot_config (key, value, description) values
  ('orch.devices.paging', '20', 'Orchestrator dashboard: devices per page')
on conflict (key) do nothing;
