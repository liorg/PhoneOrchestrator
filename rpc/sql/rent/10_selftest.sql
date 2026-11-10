-- ════════════════════════════════════════════════════════════════════════════
-- 10_selftest.sql — ניקוי נתוני ה-selftest של PhoneOrchestrator
--   POST /api/devices/selftest יוצר מכשיר SELFTEST-xxxx עם מספרים +999…
--   rpc_orch_selftest_cleanup מוחק כל מה שנוצר מהם, לפי סדר ה-FK
-- ════════════════════════════════════════════════════════════════════════════

create or replace function rpc_orch_selftest_cleanup()
returns jsonb
language plpgsql
as $$
declare
  v_dev uuid[];
  v_sim uuid[];
  v_ph  uuid[];
  x     uuid;
begin
  select coalesce(array_agg(id), '{}') into v_dev from device where device_id like 'SELFTEST-%';
  select coalesce(array_agg(id), '{}') into v_sim from sim where device_id = any (v_dev);
  select coalesce(array_agg(id), '{}') into v_ph  from phones
   where sim_id = any (v_sim) or number like '+999%';

  foreach x in array v_ph loop
    perform _rent_wipe_phone(x, '{}');
  end loop;

  delete from device_pull     where sim_id = any (v_sim) or phone_id = any (v_ph);
  delete from sms             where sim_id = any (v_sim) or phone_id = any (v_ph);
  delete from phone_templates where phone_id = any (v_ph);
  delete from rent            where phone_id = any (v_ph);
  delete from phones          where id = any (v_ph);
  delete from device          where id = any (v_dev);          -- sim → cascade

  return jsonb_build_object('ok', true, 'devices', cardinality(v_dev), 'phones', cardinality(v_ph));
end;
$$;

revoke all on function rpc_orch_selftest_cleanup() from public, anon, authenticated;
grant execute on function rpc_orch_selftest_cleanup() to service_role;
