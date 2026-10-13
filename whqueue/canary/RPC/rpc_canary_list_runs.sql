-- ═══ rpc_canary_list_runs ════════════════════════════════════════════════
drop function if exists public.rpc_canary_list_runs(timestamptz, timestamptz, int, int);

create or replace function public.rpc_canary_list_runs(
  p_from timestamptz default null,
  p_to   timestamptz default null,
  p_page int default 1,
  p_page_size int default null)
returns jsonb
language plpgsql stable as $$
declare
  v_size  int := coalesce(p_page_size, 20);
  v_page  int := greatest(coalesce(p_page, 1), 1);
  v_from  timestamptz := coalesce(p_from, now() - interval '7 days');
  v_to    timestamptz := coalesce(p_to, now());
  v_total int;
  v_items jsonb;
begin
  with r as (
    select to_jsonb(cr) j,
           coalesce((to_jsonb(cr)->>'started_at')::timestamptz,
                    (to_jsonb(cr)->>'created_at')::timestamptz) ts
    from public.canary_runs cr
  ), f as (select * from r where ts between v_from and v_to)
  select count(*) into v_total from f;

  with r as (
    select to_jsonb(cr) j,
           coalesce((to_jsonb(cr)->>'started_at')::timestamptz,
                    (to_jsonb(cr)->>'created_at')::timestamptz) ts
    from public.canary_runs cr
  ), f as (
    select * from r where ts between v_from and v_to
    order by ts desc limit v_size offset (v_page - 1) * v_size
  )
  select coalesce(jsonb_agg(
           j || jsonb_build_object(
             'started_at',   ts,
             'pass_pct',     case when coalesce((j->>'total')::int, 0) > 0
                                  then round(100.0 * coalesce((j->>'passed')::int, 0) / (j->>'total')::int)
                             end,
             'duration_sec', case when j->>'finished_at' is not null
                                  then extract(epoch from ((j->>'finished_at')::timestamptz - ts))::int
                             end)
           order by ts desc), '[]'::jsonb)
  into v_items from f;

  return jsonb_build_object(
    'items', v_items, 'total', v_total, 'page', v_page,
    'pages', greatest(ceil(v_total::numeric / v_size)::int, 1));
end $$;

-- ═══ rpc_canary_list_checks ══════════════════════════════════════════════
drop function if exists public.rpc_canary_list_checks(uuid, text, int, int);

create or replace function public.rpc_canary_list_checks(
  p_run_id uuid,
  p_status text default null,
  p_page int default 1,
  p_page_size int default null)
returns jsonb
language plpgsql stable as $$
declare
  v_size  int := coalesce(p_page_size, 50);
  v_page  int := greatest(coalesce(p_page, 1), 1);
  v_total int;
  v_items jsonb;
begin
  select count(*) into v_total
  from public.canary_checks c
  where c.run_id = p_run_id and (p_status is null or c.status = p_status);

  select coalesce(jsonb_agg(to_jsonb(x) order by x.status, x.phone_number), '[]'::jsonb)
  into v_items
  from (
    select c.* from public.canary_checks c
    where c.run_id = p_run_id and (p_status is null or c.status = p_status)
    order by c.status, c.phone_number
    limit v_size offset (v_page - 1) * v_size
  ) x;

  return jsonb_build_object(
    'items', v_items, 'total', v_total, 'page', v_page,
    'pages', greatest(ceil(v_total::numeric / v_size)::int, 1));
end $$;

-- ישן מ-v1 (מסך האירועים הוסר)
drop function if exists public.rpc_canary_list_events(uuid);

-- PostgREST רואה את השינוי מיד
notify pgrst, 'reload schema';