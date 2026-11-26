-- 013_optout_admin.sql — PhoneOrchestrator back office: opt-out list + manual unblock
-- Run after 011_optout.sql, 012_optout_guard.sql and plan/22_admin.sql (_admin_page_size).
-- Page: unwanted.html. Same rules as 22_admin.sql: SECURITY DEFINER, service_role only.

-- who unblocked from the dashboard (one shared password → caller IP, like catalog_history.actor)
alter table public.contact_optouts add column if not exists revoked_actor text;

-- ── list, newest first ───────────────────────────────────────────────────────
-- p_status: active (blocked now) | revoked | null/'' = all
-- p_search: digits of the number, the customer's email, or the UNW- ref
create or replace function public.rpc_admin_optouts(
  p_page int default 1, p_status text default 'active', p_search text default null)
returns jsonb
language plpgsql stable security definer set search_path = public, auth as $$
declare
  v_size  int  := _admin_page_size();
  v_page  int  := greatest(1, coalesce(p_page, 1));
  v_q     text := nullif(trim(coalesce(p_search, '')), '');
  v_dig   text := nullif(regexp_replace(coalesce(p_search, ''), '\D', '', 'g'), '');
  v_ref   bigint;
  v_total int;
  v_items jsonb;
  v_act   int;
begin
  if v_q ~* '^unw-?\d+$' then v_ref := regexp_replace(v_q, '\D', '', 'g')::bigint; end if;

  with f as (
    select o.*, au.email::text as email, ph.number as phone_number
      from contact_optouts o
      left join auth.users au on au.id = o.user_id
      left join phones ph     on ph.id::text = o.phone_id
     where (p_status is null or p_status = ''
            or (p_status = 'active'  and o.revoked_at is null)
            or (p_status = 'revoked' and o.revoked_at is not null))
       and (v_q is null
            or (v_ref is not null and o.id = v_ref)
            or (v_dig is not null and length(v_dig) >= 3 and o.number like '%' || v_dig || '%')
            or au.email ilike '%' || v_q || '%')
  )
  select (select count(*) from f),
         coalesce((
           select jsonb_agg(jsonb_build_object(
               'id', x.id, 'ref', optout_ref(x.id), 'number', x.number, 'source', x.source,
               'user_id', x.user_id, 'email', x.email, 'phone_id', x.phone_id, 'phone_number', x.phone_number,
               'stop_text', x.stop_text, 'lids', x.lids,
               'created_at', x.created_at, 'revoked_at', x.revoked_at, 'revoked_actor', x.revoked_actor,
               'is_active', x.revoked_at is null,
               -- contacts currently tagged 'unwanted' because of this row (page rows only)
               'contacts', case when x.revoked_at is null then (
                   select count(*) from contacts c
                    where c.tag = 'unwanted'
                      and (regexp_replace(coalesce(c.number, ''), '\D', '', 'g') = x.number
                           or (coalesce(c.lid, '') <> '' and c.lid = any (x.lids)))) end)
             order by x.created_at desc, x.id desc)
             from (select * from f order by created_at desc, id desc
                    limit v_size offset (v_page - 1) * v_size) x), '[]'::jsonb)
    into v_total, v_items;

  select count(*) into v_act from contact_optouts where revoked_at is null;

  return jsonb_build_object('items', v_items, 'total', v_total, 'page', v_page, 'page_size', v_size,
                            'pages', greatest(1, ceil(v_total::numeric / v_size)::int),
                            'active_total', v_act);
end $$;

-- ── manual unblock from the dashboard ────────────────────────────────────────
-- optout_revoke() does the work (contacts back to 'inactive'); this records who.
create or replace function public.rpc_admin_optout_revoke(p_number text, p_actor text default null)
returns jsonb
language plpgsql security definer set search_path = public as $$
declare
  v_num text := regexp_replace(coalesce(p_number, ''), '\D', '', 'g');
  v_id  bigint;
  v_n   int;
begin
  select id into v_id from contact_optouts where number = v_num and revoked_at is null;
  if v_id is null then
    raise exception 'number % is not blocked', v_num using errcode = '22023';
  end if;

  v_n := optout_revoke(v_num, null);
  update contact_optouts set revoked_actor = left(p_actor, 200) where id = v_id;

  return jsonb_build_object('ok', true, 'id', v_id, 'ref', optout_ref(v_id), 'number', v_num, 'revoked', v_n);
end $$;

revoke execute on function public.rpc_admin_optouts(int, text, text)       from public, anon, authenticated;
revoke execute on function public.rpc_admin_optout_revoke(text, text)      from public, anon, authenticated;
grant  execute on function public.rpc_admin_optouts(int, text, text)       to service_role;
grant  execute on function public.rpc_admin_optout_revoke(text, text)      to service_role;
