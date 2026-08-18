-- ============================================================================
--  PhoneOrchestrator :: DB layer   (v2 - aligned to real schema)
--  ----------------------------------------------------------------------
--  Verified against information_schema:
--    phones     : id, user_id, number, label, color, status, docker_url,
--                 docker_status, created_at, host_id, container_id,
--                 container_name, api_port, ws_port, last_health_check,
--                 error_message, creds_base64, auth_session_id,
--                 auth_revision, pairing_code, pairing_code_expiry,
--                 use_pairing_code, creds_updated_at
--                 -> NOTE: phones has NO updated_at column.
--    bot_config : key, value, description, updated_at
--
--  Port allocation: owned by WhatsAppDockerManager, not by the DB.
--  agent_hosts.port_range_start/end is NOT enforced in practice.
-- ============================================================================


-- ---------------------------------------------------------------------------
-- 0. audit table + indexes
-- ---------------------------------------------------------------------------
create table if not exists orchestrator_migrations (
    id            uuid primary key default gen_random_uuid(),
    phone_id      uuid        not null,
    from_host_id  uuid,
    to_host_id    uuid        not null,
    env           text        not null default 'preprod',
    reason        text,
    created_at    timestamptz not null default now()
);

create index if not exists ix_orch_mig_phone
    on orchestrator_migrations (phone_id, created_at desc);

create index if not exists ix_orch_mig_created
    on orchestrator_migrations (created_at desc);

create index if not exists ix_phones_host_id
    on phones (host_id);


-- ---------------------------------------------------------------------------
-- 1. config defaults (idempotent, no unique-constraint dependency)
-- ---------------------------------------------------------------------------
insert into bot_config (key, value, description)
select v.key, v.value, v.description
from (values
    ('orchestrator.env',                   'preprod', 'dev | preprod - controls which gates are enforced'),
    ('orchestrator.heartbeat_timeout_sec', '90',      'host considered dead after N sec without heartbeat'),
    ('orchestrator.ram_max_pct',           '85',      'preprod gate: max RAM %'),
    ('orchestrator.cpu_max_pct',           '85',      'preprod gate: max CPU %'),
    ('orchestrator.hosts.paging',          '20',      'UI page size - hosts list'),
    ('orchestrator.phones.paging',         '20',      'UI page size - phones drill-down')
) as v(key, value, description)
where not exists (select 1 from bot_config b where b.key = v.key);


-- ---------------------------------------------------------------------------
-- 2. rpc_orch_pick_host
--    Gates:
--      (1) status = 'active'
--      (2) heartbeat fresh
--      (3) RAM < ram_max_pct    [preprod only]
--      (4) CPU < cpu_max_pct    [preprod only]
--      (5) fewest phones wins
-- ---------------------------------------------------------------------------
create or replace function rpc_orch_pick_host(
    p_env          text default null,
    p_exclude_host uuid default null
)
returns table (
    id          uuid,
    host_name   text,
    ip_address  text,
    phone_count int,
    ram_pct     numeric,
    cpu_pct     numeric
)
language sql
stable
as $$
with cfg as (
    select
        coalesce(p_env,
                 (select value from bot_config where key = 'orchestrator.env'),
                 'preprod')                                                                                as env,
        coalesce((select value::int     from bot_config where key = 'orchestrator.heartbeat_timeout_sec'), 90) as hb_sec,
        coalesce((select value::numeric from bot_config where key = 'orchestrator.ram_max_pct'), 85)          as ram_max,
        coalesce((select value::numeric from bot_config where key = 'orchestrator.cpu_max_pct'), 85)          as cpu_max
),
candidates as (
    select
        h.id,
        h.host_name,
        h.ip_address,
        -- live count: agent_hosts.phone_count only refreshes on heartbeat,
        -- which would make a drain loop pile every phone onto one host.
        (select count(*)::int from phones p where p.host_id = h.id) as phone_count,
        case when coalesce(h.ram_total_mb, 0) = 0 then null
             else round(h.ram_used_mb::numeric * 100 / h.ram_total_mb, 2)
        end                                                          as ram_pct,
        h.cpu_percent::numeric                                       as cpu_pct,
        (h.last_heartbeat is not null
         and h.last_heartbeat > now() - make_interval(secs => c.hb_sec)) as hb_ok,
        h.max_containers,
        c.env, c.ram_max, c.cpu_max
    from agent_hosts h
    cross join cfg c
    where h.status = 'active'                                        -- gate 1
      and (p_exclude_host is null or h.id <> p_exclude_host)
)
select id, host_name, ip_address, phone_count, ram_pct, cpu_pct
from candidates
where hb_ok                                                          -- gate 2
  and phone_count < max_containers                                   -- capacity
  and (
        env <> 'preprod'
        or (coalesce(ram_pct, 0) < ram_max                           -- gate 3
            and coalesce(cpu_pct, 0) < cpu_max)                      -- gate 4
      )
order by phone_count asc,                                            -- gate 5
         coalesce(cpu_pct, 0) asc,
         host_name asc
limit 1;
$$;


-- ---------------------------------------------------------------------------
-- 3. rpc_orch_migrate_phone
--    Migration == flip phones.host_id + invalidate the stale container
--    identity. WhatsAppDockerManager on the target host picks the phone up
--    on its next sync and rebuilds the container from creds_base64.
--
--    Ports ARE cleared: observed api_port values (8154, 8161, 8970, 8990)
--    all fall outside agent_hosts.port_range_start/end (8001-8100), and
--    ws_port is always null -> WhatsAppDockerManager allocates the port
--    itself and writes it back. A carried-over port would be stale.
--
--    Deliberately NOT touched: creds_base64, auth_session_id, auth_revision
--    (these are what make the session survive the move).
-- ---------------------------------------------------------------------------
create or replace function rpc_orch_migrate_phone(
    p_phone_id uuid,
    p_env      text default null,
    p_reason   text default null
)
returns jsonb
language plpgsql
as $$
declare
    v_from uuid;
    v_env  text;
    v_to   record;
begin
    v_env := coalesce(p_env,
                      (select value from bot_config where key = 'orchestrator.env'),
                      'preprod');

    select host_id into v_from from phones where id = p_phone_id;
    if not found then
        return jsonb_build_object('ok', false, 'error', 'PHONE_NOT_FOUND');
    end if;

    select * into v_to from rpc_orch_pick_host(v_env, v_from);
    if not found then
        return jsonb_build_object(
            'ok',        false,
            'error',     'NO_ELIGIBLE_HOST',
            'env',       v_env,
            'phone_id',  p_phone_id,
            'from_host', v_from
        );
    end if;

    update phones
       set host_id        = v_to.id,
           container_id   = null,
           container_name = null,
           docker_url     = null,
           docker_status  = null,
           error_message  = null,
           api_port       = null,
           ws_port        = null
     where id = p_phone_id;

    insert into orchestrator_migrations (phone_id, from_host_id, to_host_id, env, reason)
    values (p_phone_id, v_from, v_to.id, v_env, p_reason);

    return jsonb_build_object(
        'ok',           true,
        'phone_id',     p_phone_id,
        'env',          v_env,
        'from_host',    v_from,
        'to_host',      v_to.id,
        'to_host_name', v_to.host_name,
        'reason',       p_reason
    );
end;
$$;


-- ---------------------------------------------------------------------------
-- 4. rpc_orch_drain_host  -- move every phone off an unhealthy host
-- ---------------------------------------------------------------------------
create or replace function rpc_orch_drain_host(
    p_host_id uuid,
    p_env     text default null,
    p_reason  text default 'HOST_UNHEALTHY'
)
returns jsonb
language plpgsql
as $$
declare
    v_phone   uuid;
    v_results jsonb := '[]'::jsonb;
    v_moved   int   := 0;
    v_failed  int   := 0;
    v_one     jsonb;
begin
    for v_phone in
        select id from phones where host_id = p_host_id order by id
    loop
        v_one     := rpc_orch_migrate_phone(v_phone, p_env, p_reason);
        v_results := v_results || v_one;
        if (v_one->>'ok')::boolean then
            v_moved := v_moved + 1;
        else
            v_failed := v_failed + 1;
        end if;
    end loop;

    return jsonb_build_object(
        'ok',      v_failed = 0,
        'host_id', p_host_id,
        'moved',   v_moved,
        'failed',  v_failed,
        'results', v_results
    );
end;
$$;


-- ---------------------------------------------------------------------------
-- 5. rpc_orch_list_hosts  -- UI: machine list (paged)
-- ---------------------------------------------------------------------------
create or replace function rpc_orch_list_hosts(
    p_page      int default 1,
    p_page_size int default null
)
returns jsonb
language plpgsql
stable
as $$
declare
    v_size   int;
    v_offset int;
    v_total  int;
    v_hb     int;
    v_ram    numeric;
    v_cpu    numeric;
    v_items  jsonb;
begin
    v_size := coalesce(p_page_size,
                       (select value::int from bot_config where key = 'orchestrator.hosts.paging'),
                       20);
    v_hb   := coalesce((select value::int     from bot_config where key = 'orchestrator.heartbeat_timeout_sec'), 90);
    v_ram  := coalesce((select value::numeric from bot_config where key = 'orchestrator.ram_max_pct'), 85);
    v_cpu  := coalesce((select value::numeric from bot_config where key = 'orchestrator.cpu_max_pct'), 85);

    v_offset := (greatest(p_page, 1) - 1) * v_size;

    select count(*) into v_total from agent_hosts;

    select coalesce(jsonb_agg(s.x), '[]'::jsonb)
      into v_items
    from (
        select jsonb_build_object(
            'id',              h.id,
            'host_name',       h.host_name,
            'ip_address',      h.ip_address,
            'external_ip',     h.external_ip,
            'status',          h.status,
            'last_heartbeat',  h.last_heartbeat,
            'heartbeat_ok',    (h.last_heartbeat is not null
                                and h.last_heartbeat > now() - make_interval(secs => v_hb)),
            'cpu_percent',     h.cpu_percent,
            'cpu_ok',          (coalesce(h.cpu_percent::numeric, 0) < v_cpu),
            'ram_total_mb',    h.ram_total_mb,
            'ram_used_mb',     h.ram_used_mb,
            'ram_pct',         case when coalesce(h.ram_total_mb, 0) = 0 then null
                                    else round(h.ram_used_mb::numeric * 100 / h.ram_total_mb, 1) end,
            'ram_ok',          case when coalesce(h.ram_total_mb, 0) = 0 then true
                                    else (h.ram_used_mb::numeric * 100 / h.ram_total_mb) < v_ram end,
            'disk_total_gb',   h.disk_total_gb,
            'disk_used_gb',    h.disk_used_gb,
            'disk_pct',        case when coalesce(h.disk_total_gb, 0) = 0 then null
                                    else round(h.disk_used_gb::numeric * 100 / h.disk_total_gb, 1) end,
            'phone_count',     h.phone_count,
            'container_count', h.container_count,
            'max_containers',  h.max_containers,
            'eligible',        (
                h.status = 'active'
                and h.last_heartbeat is not null
                and h.last_heartbeat > now() - make_interval(secs => v_hb)
                and h.phone_count < h.max_containers
                and coalesce(h.cpu_percent::numeric, 0) < v_cpu
                and (coalesce(h.ram_total_mb, 0) = 0
                     or (h.ram_used_mb::numeric * 100 / h.ram_total_mb) < v_ram)
            )
        ) as x
        from agent_hosts h
        order by h.host_name
        offset v_offset
        limit  v_size
    ) s;

    return jsonb_build_object(
        'items',     v_items,
        'total',     v_total,
        'page',      greatest(p_page, 1),
        'page_size', v_size,
        'pages',     greatest(ceil(v_total::numeric / v_size)::int, 1)
    );
end;
$$;


-- ---------------------------------------------------------------------------
-- 6. rpc_orch_list_host_phones  -- UI: drill-down (paged)
--    Secrets stripped: creds_base64, auth_session_id, pairing_code.
-- ---------------------------------------------------------------------------
create or replace function rpc_orch_list_host_phones(
    p_host_id   uuid,
    p_page      int default 1,
    p_page_size int default null
)
returns jsonb
language plpgsql
stable
as $$
declare
    v_size   int;
    v_offset int;
    v_total  int;
    v_items  jsonb;
begin
    v_size := coalesce(p_page_size,
                       (select value::int from bot_config where key = 'orchestrator.phones.paging'),
                       20);
    v_offset := (greatest(p_page, 1) - 1) * v_size;

    select count(*) into v_total from phones where host_id = p_host_id;

    select coalesce(jsonb_agg(s.x), '[]'::jsonb)
      into v_items
    from (
        select jsonb_build_object(
            'id',                phones.id,
            'number',            phones.number,
            'label',             phones.label,
            'color',             phones.color,
            'status',            phones.status,
            'docker_status',     phones.docker_status,
            'docker_url',        phones.docker_url,
            'container_id',      phones.container_id,
            'container_name',    phones.container_name,
            'api_port',          phones.api_port,
            'ws_port',           phones.ws_port,
            'last_health_check', phones.last_health_check,
            'error_message',     phones.error_message,
            'auth_revision',     phones.auth_revision,
            'has_creds',         (phones.creds_base64 is not null),
            'creds_updated_at',  phones.creds_updated_at,
            'created_at',        phones.created_at,
            'user_id',           phones.user_id,
            'host_id',           phones.host_id
        ) as x
        from phones
        where phones.host_id = p_host_id
        order by phones.number
        offset v_offset
        limit  v_size
    ) s;

    return jsonb_build_object(
        'items',     v_items,
        'total',     v_total,
        'page',      greatest(p_page, 1),
        'page_size', v_size,
        'pages',     greatest(ceil(v_total::numeric / v_size)::int, 1),
        'host_id',   p_host_id
    );
end;
$$;
