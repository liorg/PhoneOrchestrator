-- ════════════════════════════════════════════════════════════════════════════
-- 12_orch_sms.sql — מסך SMS במסך הניהול (PhoneOrchestrator, sms.html)
--   עמודות forward_*  מה SmsRelay עשה עם ה-SMS מול ה-Manager (debug)
--   rpc_sms_forwarded כתיבת הסטטוס — SmsRelay אחרי כל החלטה
--   rpc_orch_list_sms רשימה מדופדפת + פילטרים (מכשיר / SIM / טלפון / חיפוש / סטטוס)
--   rpc_orch_sms      SMS אחד: הכול כולל raw
-- נתיב legacy (POST /api/hub/sms → HubController) לא כותב forward_* → forward_status ריק.
-- ════════════════════════════════════════════════════════════════════════════

alter table sms add column if not exists forward_status   text;         -- forwarded | rejected | retrying | gave_up | too_old | no_phone | no_manager
alter table sms add column if not exists forward_error    text;
alter table sms add column if not exists forward_attempts int;
alter table sms add column if not exists forwarded_at     timestamptz;  -- רגע ההחלטה האחרונה (לא רק הצלחה)

create index if not exists sms_sort_at_idx on sms (sort_at desc, id desc);


create or replace function rpc_sms_forwarded(
  p_msg_id uuid, p_status text, p_error text default null, p_attempts int default null)
returns jsonb
language plpgsql
as $$
declare
  v_n int;
begin
  update sms
     set forward_status   = p_status,
         forward_error    = left(p_error, 1000),
         forward_attempts = p_attempts,
         forwarded_at     = now()
   where msg_id = p_msg_id;
  get diagnostics v_n = row_count;
  return jsonb_build_object('ok', v_n > 0);
end;
$$;


-- p_status: null = הכול · 'legacy' = forward_status ריק · אחרת ערך מדויק
create or replace function rpc_orch_list_sms(
  p_page int default 1, p_page_size int default null,
  p_device text default null, p_sim_id uuid default null, p_phone_id uuid default null,
  p_search text default null, p_status text default null)
returns jsonb
language plpgsql
as $$
declare
  v_size  int := least(greatest(coalesce(p_page_size,
                   (select nullif(value, '')::int from bot_config where key = 'orch.sms.paging'), 50), 1), 200);
  v_page  int := greatest(coalesce(p_page, 1), 1);
  v_dev   text := nullif(trim(coalesce(p_device, '')), '');
  v_q     text := nullif(trim(coalesce(p_search, '')), '');
  v_st    text := nullif(trim(coalesce(p_status, '')), '');
  v_total int;
  v_pages int;
  v_items jsonb;
begin
  drop table if exists _f;
  create temp table _f on commit drop as
  select m.id
    from sms m
    left join sim s    on s.id = m.sim_id
    left join device d on d.id = s.device_id
   where (v_dev is null or d.device_id ilike '%' || v_dev || '%' or m.raw->>'device_id' ilike '%' || v_dev || '%')
     and (p_sim_id   is null or m.sim_id   = p_sim_id)
     and (p_phone_id is null or m.phone_id = p_phone_id)
     and (v_q is null or m.body ilike '%' || v_q || '%' or m.target_phone ilike '%' || v_q || '%'
                      or m.source_phone ilike '%' || v_q || '%' or m.msg_id::text = v_q)
     and (v_st is null or (v_st = 'legacy' and m.forward_status is null) or m.forward_status = v_st);

  select count(*) into v_total from _f;
  v_pages := greatest(1, ceil(v_total::numeric / v_size)::int);
  v_page  := least(v_page, v_pages);

  select coalesce(jsonb_agg(t.x order by t.sort_at desc, t.id desc), '[]'::jsonb) into v_items
  from (
    select m.sort_at, m.id,
           jsonb_build_object(
             'id', m.id, 'msg_id', m.msg_id,
             'device_id', coalesce(d.device_id, m.raw->>'device_id'), 'device_uuid', d.id,
             'slot', coalesce(s.slot, (m.raw->>'sim_slot')::int),
             'sim_id', m.sim_id, 'phone_id', m.phone_id,
             'number', coalesce(ph.number, m.source_phone),
             'from', m.target_phone, 'body', left(m.body, 400),
             'sent_at', m.delivery_date, 'received_at', m.received_at,
             'hub_received_at', m.hub_received_at, 'sort_at', m.sort_at,
             'forward_status', m.forward_status, 'forward_error', m.forward_error,
             'forward_attempts', m.forward_attempts, 'forwarded_at', m.forwarded_at,
             'owner', u.email
           ) as x
      from sms m
      join _f        on _f.id = m.id
      left join sim s    on s.id = m.sim_id
      left join device d on d.id = s.device_id
      left join phones ph on ph.id = m.phone_id
      left join users u   on u.id = ph.user_id
     order by m.sort_at desc, m.id desc
     limit v_size offset (v_page - 1) * v_size
  ) t;

  return jsonb_build_object('items', v_items, 'total', v_total, 'page', v_page,
                            'page_size', v_size, 'pages', v_pages);
end;
$$;


create or replace function rpc_orch_sms(p_id uuid)
returns jsonb
language sql
as $$
  select to_jsonb(m) || jsonb_build_object(
           'device_id', coalesce(d.device_id, m.raw->>'device_id'),
           'slot', coalesce(s.slot, (m.raw->>'sim_slot')::int),
           'number', ph.number, 'owner', u.email)
    from sms m
    left join sim s    on s.id = m.sim_id
    left join device d on d.id = s.device_id
    left join phones ph on ph.id = m.phone_id
    left join users u   on u.id = ph.user_id
   where m.id = p_id;
$$;


do $$
declare f text;
begin
  foreach f in array array[
    'rpc_sms_forwarded(uuid,text,text,int)',
    'rpc_orch_list_sms(int,int,text,uuid,uuid,text,text)',
    'rpc_orch_sms(uuid)'
  ] loop
    execute format('revoke all on function %s from public, anon, authenticated', f);
    execute format('grant execute on function %s to service_role', f);
  end loop;
end $$;

insert into bot_config (key, value, description) values
  ('orch.sms.paging', '50', 'Orchestrator dashboard: SMS per page')
on conflict (key) do nothing;
