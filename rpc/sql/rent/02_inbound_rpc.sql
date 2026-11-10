-- ════════════════════════════════════════════════════════════════════════════
-- 02_inbound_rpc.sql — מה ש-whqueue קורא: DeviceUpdate + inbound SMS
-- ════════════════════════════════════════════════════════════════════════════

create or replace function _rent_system_user()
returns uuid
language sql
stable
as $$
  select id from users where email = 'system@grossman.bot' limit 1
$$;


-- p: {device_id (serial), hub_id, update_from (IP של ה-docker), ip, status, adb_state,
--     model, android, agent_version,
--     sims: [{slot, subscription_id, phone_number, carrier, wa_type}]}
-- status: add (USB חובר) / remove (נותק). adb_state: device / unauthorized / offline
-- wa_type: whatsapp / business (גם com.whatsapp / com.whatsapp.w4b). null = לא מותקן.
--          מפתח שלא נשלח בכלל = לא לגעת בערך הקיים
-- SIM עם מספר → שורה ב-phones (user_id=system) אם אין, מקושרת ב-sim_id
create or replace function rpc_device_update(p jsonb)
returns jsonb
language plpgsql
as $$
#variable_conflict use_column
declare
  v_serial  text := nullif(trim(p->>'device_id'), '');
  v_status  text := case when lower(coalesce(p->>'status', 'add')) in ('remove', 'removed', 'missing')
                           then 'remove' else 'add' end;
  v_wa      text;
  v_dev     uuid;
  s         jsonb;
  v_slot    int;
  v_num     text;
  v_sim     uuid;
  v_phone   uuid;
  v_sys     uuid;
  v_slots   int[] := '{}';
  v_out     jsonb := '[]'::jsonb;
begin
  if v_serial is null then
    raise exception 'device_id required';
  end if;

  insert into device (device_id, hub_id, update_from, ip, model, android, agent_version,
                      status, adb_state, last_seen_at)
  values (v_serial, nullif(p->>'hub_id', ''), nullif(p->>'update_from', ''), nullif(p->>'ip', ''),
          nullif(p->>'model', ''), nullif(p->>'android', ''), (p->>'agent_version')::int,
          v_status, case when v_status = 'remove' then null else nullif(p->>'adb_state', '') end, now())
  on conflict (device_id) do update
    set hub_id        = coalesce(excluded.hub_id, device.hub_id),
        update_from   = coalesce(excluded.update_from, device.update_from),
        adb_state     = case when excluded.status = 'remove' then null
                             else coalesce(excluded.adb_state, device.adb_state) end,
        ip            = coalesce(excluded.ip, device.ip),
        model         = coalesce(excluded.model, device.model),
        android       = coalesce(excluded.android, device.android),
        agent_version = coalesce(excluded.agent_version, device.agent_version),
        status        = excluded.status,
        last_seen_at  = now(),
        modified_at   = now()
  returning id into v_dev;

  for s in select * from jsonb_array_elements(coalesce(p->'sims', '[]'::jsonb)) loop
    continue when s->>'slot' is null;
    v_slot := (s->>'slot')::int;
    v_num  := nullif(trim(coalesce(s->>'phone_number', '')), '');
    v_slots := v_slots || v_slot;
    v_wa := case lower(coalesce(s->>'wa_type', ''))
              when 'whatsapp' then 'whatsapp' when 'com.whatsapp' then 'whatsapp'
              when 'business' then 'business' when 'com.whatsapp.w4b' then 'business' end;

    -- SIM שעבר מכשיר/slot: משחררים את המספר מהשורה הישנה
    if v_num is not null then
      update sim set phone_number = null, status = 'inactive', modified_at = now()
       where phone_number_clean(phone_number) = phone_number_clean(v_num)
         and not (device_id = v_dev and slot = v_slot);
    end if;

    insert into sim (device_id, slot, subscription_id, phone_number, carrier, wa_type, status)
    values (v_dev, v_slot, (s->>'subscription_id')::int, v_num, nullif(s->>'carrier', ''), v_wa, 'active')
    on conflict (device_id, slot) do update
      set subscription_id = coalesce(excluded.subscription_id, sim.subscription_id),
          wa_type         = case when s ? 'wa_type' then excluded.wa_type else sim.wa_type end,
          phone_number    = coalesce(excluded.phone_number, sim.phone_number),
          carrier         = coalesce(excluded.carrier, sim.carrier),
          status          = 'active',
          modified_at     = now()
    returning id, phone_number into v_sim, v_num;

    v_phone := null;
    if v_num is not null then
      select id into v_phone from phones
       where phone_number_clean(number) = phone_number_clean(v_num)
       limit 1;

      if v_phone is null then
        v_sys := coalesce(v_sys, _rent_system_user());
        if v_sys is null then
          raise exception 'system user (system@grossman.bot) not found';
        end if;
        update phones set sim_id = null where sim_id = v_sim;
        insert into phones (user_id, number, status, docker_status, provider, sim_id)
        values (v_sys, v_num, 'inactive', 'stopped', 'baileys', v_sim)
        returning id into v_phone;
      else
        update phones set sim_id = null where sim_id = v_sim and id <> v_phone;
        update phones set sim_id = v_sim where id = v_phone and sim_id is distinct from v_sim;
      end if;
    end if;

    v_out := v_out || jsonb_build_object('slot', v_slot, 'sim_id', v_sim, 'phone_number', v_num,
                                         'wa_type', (select wa_type from sim where id = v_sim), 'phone_id', v_phone);
  end loop;

  -- slot שלא דווח (רק כשה-hub שלח רשימת sims) → inactive
  if p ? 'sims' and v_status = 'add' then
    update sim set status = 'inactive', modified_at = now()
     where device_id = v_dev and not (slot = any (v_slots)) and status <> 'inactive';
  end if;

  return jsonb_build_object('ok', true, 'device', v_dev, 'device_id', v_serial,
                            'status', v_status, 'sims', v_out);
end;
$$;


-- p: {msg_id, device_id (serial), sim_slot, source_phone?, from, body, sent_at, received_at, hub_received_at}
-- phone_id: לפי source_phone, ואם אין — device+slot → sim → phones.sim_id.
-- SMS בלי טלפון מזוהה נשמר עם phone_id=null (לא מאבדים). כפילות → inserted=false
create or replace function rpc_sms_inbound(p jsonb)
returns jsonb
language plpgsql
as $$
declare
  v_msg     uuid := (p->>'msg_id')::uuid;
  v_sim     uuid;
  v_simnum  text;
  v_source  text;
  v_phone   uuid;
  v_id      uuid;
begin
  if v_msg is null then
    raise exception 'msg_id required';
  end if;

  select s.id, s.phone_number into v_sim, v_simnum
    from sim s join device d on d.id = s.device_id
   where d.device_id = p->>'device_id' and s.slot = (p->>'sim_slot')::int;

  v_source := coalesce(nullif(trim(coalesce(p->>'source_phone', '')), ''), v_simnum);

  if v_source is not null then
    select id into v_phone from phones
     where phone_number_clean(number) = phone_number_clean(v_source) limit 1;
  end if;
  if v_phone is null and v_sim is not null then
    select id into v_phone from phones where sim_id = v_sim;
  end if;

  insert into sms (msg_id, phone_id, sim_id, source_phone, target_phone, body,
                   delivery_date, received_at, hub_received_at, raw)
  values (v_msg, v_phone, v_sim, v_source, nullif(p->>'from', ''), p->>'body',
          (p->>'sent_at')::timestamptz, (p->>'received_at')::timestamptz,
          (p->>'hub_received_at')::timestamptz, p)
  on conflict (msg_id) do nothing
  returning id into v_id;

  return jsonb_build_object('ok', true, 'inserted', v_id is not null, 'sms_id', v_id, 'phone_id', v_phone);
end;
$$;
