-- ════════════════════════════════════════════════════════════════════════════
-- 06_grants_seed.sql — service_role בלבד + ברירות מחדל ב-bot_config
-- ════════════════════════════════════════════════════════════════════════════

do $$
declare f text;
begin
  foreach f in array array[
    'rpc_device_update(jsonb)',
    'rpc_sms_inbound(jsonb)',
    'rpc_rent_start(uuid,uuid,numeric,text[])',
    'rpc_rent_end(uuid,uuid,text,text[])',
    'rpc_rent_expire(text[])',
    'rpc_rent_list(uuid,int,int,text)',
    'rpc_sms_threads(uuid,int,int,text)',
    'rpc_sms_thread(uuid,text,timestamptz,uuid,int)'
  ] loop
    execute format('revoke all on function %s from public, anon, authenticated', f);
    execute format('grant execute on function %s to service_role', f);
  end loop;

  -- פנימיים — לא ל-anon / authenticated. service_role צריך EXECUTE כי ה-RPC-ים
  -- (security invoker) קוראים להם בשמו
  execute 'revoke all on function _rent_wipe_phone(uuid,text[]) from public, anon, authenticated';
  execute 'revoke all on function _rent_system_user() from public, anon, authenticated';
  execute 'grant execute on function _rent_wipe_phone(uuid,text[]) to service_role';
  execute 'grant execute on function _rent_system_user() to service_role';
end $$;

insert into bot_config (key, value, description) values
  ('rent.hours',        '1',  'Rented phones: rental length in hours (fractions allowed, e.g. 0.05 = 3 min for dev)'),
  ('rent.paging',       '10', 'Rented phones: rows per page'),
  ('sms.paging',        '20', 'SMS tab: senders per page'),
  ('sms.thread_limit',  '30', 'SMS dialog: messages per scroll load')
on conflict (key) do nothing;

delete from bot_config where key = 'rent.free_minutes';   -- הוחלף ב-rent.hours
