-- ════════════════════════════════════════════════════════════════════════════
-- canary_wa_events.sql — הלוג של WhatsApp בטלפון ה-canary (ה-prober, +972507251926)
--   canary_wa_events     כל אירוע שה-prober שולח ל-/canary/inbound + כל /send/template
--   rpc_canary_wa_log    כתיבה (CanaryWaLog ב-runner). ניקוי ישנים מ-canary.wa_events.days
--   rpc_canary_wa_list   רשימה מדופדפת: dir (in|out|status|system), חיפוש
--   rpc_canary_wa_event  אירוע אחד עם raw
-- קודי OTP לא נשמרים: CanaryWaLog ממסך ספרות ביוצאות ולא כותב params של OTP.
-- ════════════════════════════════════════════════════════════════════════════

create table if not exists canary_wa_events (
  id             bigserial primary key,
  created_at     timestamptz not null default now(),
  event          text,
  direction      text not null,          -- in | out | status | system
  wa_from        text,
  wa_to          text,
  text           text,
  wa_message_id  text,
  status         int,                    -- message_status: 1 sent · 2 delivered · 3 read · 4 played · -1 failed
  check_code     text,                   -- ACK / CHECK ID שזוהה בטקסט
  verdict        text,                   -- מה CanaryStore עשה (passed, late_ack, delivery=N, sent, send_failed, error…)
  note           text,
  raw            jsonb
);

create index if not exists canary_wa_events_created_idx on canary_wa_events (created_at desc, id desc);
create index if not exists canary_wa_events_code_idx    on canary_wa_events (check_code) where check_code is not null;

alter table canary_wa_events enable row level security;   -- service_role בלבד


create or replace function rpc_canary_wa_log(p jsonb)
returns jsonb
language plpgsql
as $$
declare
  v_id   bigint;
  v_days int := coalesce((select nullif(value, '')::int from bot_config where key = 'canary.wa_events.days'), 14);
begin
  insert into canary_wa_events (event, direction, wa_from, wa_to, text, wa_message_id, status,
                                check_code, verdict, note, raw)
  values (p->>'event', coalesce(nullif(p->>'direction', ''), 'system'), p->>'wa_from', p->>'wa_to',
          left(p->>'text', 2000), p->>'wa_message_id', (p->>'status')::int,
          p->>'check_code', p->>'verdict', left(p->>'note', 1000), p->'raw')
  returning id into v_id;

  -- ניקוי בערך פעם ב-100 כתיבות — בלי job נפרד
  if random() < 0.01 then
    delete from canary_wa_events where created_at < now() - make_interval(days => v_days);
  end if;

  return jsonb_build_object('ok', true, 'id', v_id);
end;
$$;


create or replace function rpc_canary_wa_list(
  p_page int default 1, p_page_size int default null, p_dir text default null, p_search text default null)
returns jsonb
language plpgsql
as $$
declare
  v_size  int := least(greatest(coalesce(p_page_size,
                   (select nullif(value, '')::int from bot_config where key = 'canary.wa_events.paging'), 50), 1), 200);
  v_page  int := greatest(coalesce(p_page, 1), 1);
  v_dir   text := nullif(trim(coalesce(p_dir, '')), '');
  v_q     text := nullif(trim(coalesce(p_search, '')), '');
  v_total int;
  v_pages int;
  v_items jsonb;
begin
  select count(*) into v_total
    from canary_wa_events e
   where (v_dir is null or e.direction = v_dir)
     and (v_q is null or e.text ilike '%' || v_q || '%' or e.wa_from ilike '%' || v_q || '%'
                      or e.wa_to ilike '%' || v_q || '%' or e.check_code = upper(v_q)
                      or e.wa_message_id = v_q or e.event = v_q or e.verdict = v_q);

  v_pages := greatest(1, ceil(v_total::numeric / v_size)::int);
  v_page  := least(v_page, v_pages);

  select coalesce(jsonb_agg(to_jsonb(t) order by t.created_at desc, t.id desc), '[]'::jsonb) into v_items
  from (
    select e.id, e.created_at, e.event, e.direction, e.wa_from, e.wa_to, left(e.text, 300) as text,
           e.wa_message_id, e.status, e.check_code, e.verdict, e.note
      from canary_wa_events e
     where (v_dir is null or e.direction = v_dir)
       and (v_q is null or e.text ilike '%' || v_q || '%' or e.wa_from ilike '%' || v_q || '%'
                        or e.wa_to ilike '%' || v_q || '%' or e.check_code = upper(v_q)
                        or e.wa_message_id = v_q or e.event = v_q or e.verdict = v_q)
     order by e.created_at desc, e.id desc
     limit v_size offset (v_page - 1) * v_size
  ) t;

  return jsonb_build_object('items', v_items, 'total', v_total, 'page', v_page,
                            'page_size', v_size, 'pages', v_pages);
end;
$$;


create or replace function rpc_canary_wa_event(p_id bigint)
returns jsonb
language sql
as $$
  select to_jsonb(e) from canary_wa_events e where e.id = p_id;
$$;


do $$
declare f text;
begin
  foreach f in array array[
    'rpc_canary_wa_log(jsonb)',
    'rpc_canary_wa_list(int,int,text,text)',
    'rpc_canary_wa_event(bigint)'
  ] loop
    execute format('revoke all on function %s from public, anon, authenticated', f);
    execute format('grant execute on function %s to service_role', f);
  end loop;
end $$;

insert into bot_config (key, value, description) values
  ('canary.wa_events.days',   '14', 'Canary WhatsApp log: days to keep'),
  ('canary.wa_events.paging', '50', 'Canary WhatsApp log: events per page')
on conflict (key) do nothing;
