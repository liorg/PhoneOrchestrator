-- ════════════════════════════════════════════════════════════════════════════
-- 03_wipe.sql — "לנקות שולחן": כל מה שקשור לטלפון, לפי סדר ה-FK (02_fk_tree).
-- פנימי — נקרא רק מ-rpc_rent_start / rpc_rent_end. לא מורשה לאף role.
--
-- לא נמחק: rent (היסטוריה) · תבניות ב-p_keep_templates · תרחישי is_system
--           · phone_templates/cloudapi_registry של טלפונים אחרים · executions
--             (source_scenario_id → SET NULL לבד)
-- ════════════════════════════════════════════════════════════════════════════

create or replace function _rent_wipe_phone(p_phone uuid, p_keep_templates text[] default '{}')
returns jsonb
language plpgsql
as $$
declare
  v_contacts uuid[];
  v_calls    uuid[];
  v_scen     uuid[];
  v_sched    uuid[];
  v_sms      int;
  v_tpl      int;
begin
  select coalesce(array_agg(id), '{}') into v_contacts from contacts  where phone_id = p_phone;
  select coalesce(array_agg(id), '{}') into v_calls    from calls     where phone_id = p_phone;
  select coalesce(array_agg(id), '{}') into v_scen     from scenarios where phone_id = p_phone and not is_system;
  select coalesce(array_agg(id), '{}') into v_sched    from schedules
   where phone_id = p_phone or scenario_id = any (v_scen);

  -- NO ACTION — קודם הילדים
  delete from execution_links
   where phone_id = p_phone or contact_id = any (v_contacts)
      or scenario_id = any (v_scen) or schedule_id = any (v_sched);
  delete from scenario_runs
   where phone_id = p_phone or call_id = any (v_calls) or scenario_id = any (v_scen);
  delete from webhook_messages
   where phone_id = p_phone or contact_id = any (v_contacts) or call_id = any (v_calls);
  delete from messages
   where phone_id = p_phone or contact_id = any (v_contacts) or call_id = any (v_calls);
  delete from ping_sender
   where phone_id = p_phone or contact_id = any (v_contacts);
  delete from message_events            where phone_id = p_phone;
  delete from phone_provisioning_events where phone_id = p_phone;
  delete from notifications             where phone_id = p_phone;
  delete from sender_log                where phone_id = p_phone;

  delete from schedules where id = any (v_sched);
  delete from calls     where id = any (v_calls);

  update contacts set parent_contact_id = null where parent_contact_id = any (v_contacts);
  delete from contacts where id = any (v_contacts);

  update scenarios set source_scenario_id = null where source_scenario_id = any (v_scen);
  delete from scenarios where id = any (v_scen);

  delete from phone_templates
   where phone_id = p_phone and not (name = any (coalesce(p_keep_templates, '{}')));
  get diagnostics v_tpl = row_count;

  delete from sms where phone_id = p_phone;
  get diagnostics v_sms = row_count;

  return jsonb_build_object(
    'contacts', cardinality(v_contacts), 'calls', cardinality(v_calls),
    'scenarios', cardinality(v_scen), 'schedules', cardinality(v_sched),
    'templates', v_tpl, 'sms', v_sms);
end;
$$;
