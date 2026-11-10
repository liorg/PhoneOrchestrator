-- ════════════════════════════════════════════════════════════════════════════
-- 05_sms_read_rpc.sql — טאב SMS ב-PhoneDetail
--   rpc_sms_threads — רשימה מדופדפת לפי שולח (target_phone): אחרונה, כמות
--   rpc_sms_thread  — הדיאלוג: keyset (sort_at, id) לגלילה אוטומטית אחורה
-- ההרשאה (הטלפון של המשתמש) נבדקת ב-RentDevice.py לפני הקריאה.
-- ════════════════════════════════════════════════════════════════════════════

create or replace function rpc_sms_threads(
  p_phone_id uuid, p_page int default 1, p_page_size int default 20, p_search text default null)
returns jsonb
language plpgsql
as $$
declare
  v_size   int  := least(greatest(coalesce(p_page_size, 20), 1), 100);
  v_page   int  := greatest(coalesce(p_page, 1), 1);
  v_q      text := nullif(trim(coalesce(p_search, '')), '');
  v_total  int;
  v_pages  int;
  v_items  jsonb;
begin
  select count(distinct coalesce(target_phone, '')) into v_total
    from sms
   where phone_id = p_phone_id
     and (v_q is null or target_phone ilike '%' || v_q || '%' or body ilike '%' || v_q || '%');

  v_pages := greatest(1, ceil(v_total::numeric / v_size)::int);
  v_page  := least(v_page, v_pages);

  select coalesce(jsonb_agg(to_jsonb(t) order by t.last_at desc), '[]'::jsonb) into v_items
  from (
    select * from (
      select distinct on (coalesce(m.target_phone, ''))
             m.target_phone,
             m.body     as last_body,
             m.sort_at  as last_at,
             count(*) over (partition by coalesce(m.target_phone, '')) as count
        from sms m
       where m.phone_id = p_phone_id
         and (v_q is null or m.target_phone ilike '%' || v_q || '%' or m.body ilike '%' || v_q || '%')
       order by coalesce(m.target_phone, ''), m.sort_at desc, m.id desc
    ) d
    order by d.last_at desc
    limit v_size offset (v_page - 1) * v_size
  ) t;

  return jsonb_build_object('items', v_items, 'total', v_total, 'page', v_page,
                            'page_size', v_size, 'total_pages', v_pages);
end;
$$;


-- p_before_at/p_before_id = האיבר הישן ביותר שכבר מוצג. null = מהחדש ביותר.
-- items מהחדש לישן; ה-UI הופך לתצוגה.
create or replace function rpc_sms_thread(
  p_phone_id uuid, p_target text, p_before_at timestamptz default null,
  p_before_id uuid default null, p_limit int default 30)
returns jsonb
language plpgsql
as $$
declare
  v_lim   int := least(greatest(coalesce(p_limit, 30), 1), 100);
  v_items jsonb;
  v_n     int;
begin
  select coalesce(jsonb_agg(to_jsonb(t) order by t.sort_at desc, t.id desc), '[]'::jsonb),
         count(*)
    into v_items, v_n
  from (
    select id, msg_id, source_phone, target_phone, body, delivery_date, received_at, sort_at
      from sms
     where phone_id = p_phone_id
       and coalesce(target_phone, '') = coalesce(p_target, '')
       and (p_before_at is null or (sort_at, id) < (p_before_at, coalesce(p_before_id, 'ffffffff-ffff-ffff-ffff-ffffffffffff'::uuid)))
     order by sort_at desc, id desc
     limit v_lim + 1
  ) t;

  -- v_lim + 1 → יש עוד
  if v_n > v_lim then
    v_items := v_items - v_lim;
  end if;

  return jsonb_build_object('items', v_items, 'has_more', v_n > v_lim);
end;
$$;
