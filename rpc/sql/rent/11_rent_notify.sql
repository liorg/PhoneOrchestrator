-- ════════════════════════════════════════════════════════════════════════════
-- 11_rent_notify.sql — sweep של השכרות + התראות ללקוח
--
--   rpc_rent_sweep(keep)   נקרא מ-PhoneOrchestrator (RentSweeper) כל RentSweepSeconds
--     1. אזהרה  — עבר rent.warn_percent% מזמן ההשכרה → notification warning, פעם אחת (warn_notified_at)
--     2. פקיעה  — end_time עבר → rpc_rent_end(expired) → ניקוי + חזרה למאגר
--                 → notification error "ההשכרה הסתיימה" (end_notified_at)
--   rpc_rent_expire(keep)  נשאר בשביל RentDevice.py (backend) — עכשיו עובר דרך ה-sweep,
--                          כך שגם הוא שולח התראות. שני ה-runners בטוחים יחד (skip locked + דגלים).
--
-- notifications.phone_id = null בכוונה: _rent_wipe_phone מוחק notifications לפי phone_id,
-- וההתראה על הסיום נוצרת אחרי הניקוי ושייכת ללקוח, לא לטלפון.
-- המייל יוצא דרך NotificationMailer לפי users.notify_email / notify_errors_only.
-- ════════════════════════════════════════════════════════════════════════════

alter table rent add column if not exists warn_notified_at timestamptz;
alter table rent add column if not exists end_notified_at  timestamptz;

insert into bot_config (key, value, description) values
  ('rent.warn_percent',   '80',
   'rent: notify the customer when this % of the rental time has passed (0 = off)'),
  ('rent.keep_templates', 'check_contact,hello_world',
   'rent: templates kept by the wipe (comma separated) when the sweep is called without a list')
on conflict (key) do nothing;


-- התראה בשפה ובאזור הזמן של הלקוח. כישלון בהכנסה לא מפיל את ה-sweep
create or replace function _rent_notify(p_user uuid, p_kind text, p_number text, p_end timestamptz)
returns void
language plpgsql
as $$
declare
  v_lang  text;
  v_tz    text;
  v_at    text;
  v_title text;
  v_msg   text;
  v_level text := case p_kind when 'expired' then 'error' else 'warning' end;
begin
  select coalesce(nullif(lang, ''), 'he'), nullif(timezone, '') into v_lang, v_tz
    from users where id = p_user;
  if v_tz is null or not exists (select 1 from pg_timezone_names where name = v_tz) then
    v_tz := 'Asia/Jerusalem';
  end if;
  v_at := to_char(p_end at time zone v_tz, 'DD/MM HH24:MI');

  if p_kind = 'warn' then
    v_title := case v_lang
      when 'en' then 'Rental ending soon'
      when 'ru' then 'Аренда скоро закончится'
      when 'ar' then 'الاستئجار على وشك الانتهاء'
      else 'ההשכרה עומדת להסתיים' end;
    v_msg := case v_lang
      when 'en' then format('The rental of %s ends at %s. At the end everything on the phone is deleted.', p_number, v_at)
      when 'ru' then format('Аренда %s заканчивается в %s. После окончания все данные телефона удаляются.', p_number, v_at)
      when 'ar' then format('ينتهي استئجار %s في %s. عند الانتهاء يُحذف كل ما على الهاتف.', p_number, v_at)
      else format('ההשכרה של %s מסתיימת ב-%s. בסיום כל הנתונים בטלפון יימחקו.', p_number, v_at) end;
  else
    v_title := case v_lang
      when 'en' then 'Rental ended'
      when 'ru' then 'Аренда закончилась'
      when 'ar' then 'انتهى الاستئجار'
      else 'ההשכרה הסתיימה' end;
    v_msg := case v_lang
      when 'en' then format('The rental of %s ended at %s. The phone was returned to the pool and its data was deleted.', p_number, v_at)
      when 'ru' then format('Аренда %s закончилась в %s. Телефон возвращён в пул, его данные удалены.', p_number, v_at)
      when 'ar' then format('انتهى استئجار %s في %s. أُعيد الهاتف إلى المجموعة وحُذفت بياناته.', p_number, v_at)
      else format('ההשכרה של %s הסתיימה ב-%s. הטלפון חזר למאגר והנתונים שבו נמחקו.', p_number, v_at) end;
  end if;

  begin
    insert into notifications (user_id, title, message, log_level)
    values (p_user, v_title, v_msg, v_level);
  exception when others then
    raise warning '_rent_notify % user=% failed: %', p_kind, p_user, sqlerrm;
  end;
end;
$$;


create or replace function rpc_rent_sweep(p_keep_templates text[] default null)
returns jsonb
language plpgsql
as $$
declare
  v_pct    numeric := coalesce((select nullif(value, '')::numeric from bot_config where key = 'rent.warn_percent'), 80);
  v_keep   text[]  := coalesce(p_keep_templates,
                        string_to_array(replace(coalesce(
                          (select value from bot_config where key = 'rent.keep_templates'),
                          'check_contact,hello_world'), ' ', ''), ','));
  r        record;
  v_res    jsonb;
  v_warned jsonb := '[]'::jsonb;
  v_ended  jsonb := '[]'::jsonb;
begin
  -- 1. אזהרה לפני סיום
  if v_pct > 0 and v_pct < 100 then
    for r in
      select rt.id, rt.phone_id, rt.user_id, rt.end_time, ph.number
        from rent rt join phones ph on ph.id = rt.phone_id
       where rt.status = 'rent'
         and rt.warn_notified_at is null
         and rt.end_time > now()
         and now() >= rt.start_time + (rt.end_time - rt.start_time) * (v_pct / 100.0)
       for update of rt skip locked
    loop
      update rent set warn_notified_at = now() where id = r.id;
      perform _rent_notify(r.user_id, 'warn', r.number, r.end_time);
      v_warned := v_warned || jsonb_build_object('phone_id', r.phone_id, 'user_id', r.user_id, 'number', r.number);
    end loop;
  end if;

  -- 2. פקיעה → ניקוי + חזרה למאגר + התראת error
  for r in
    select rt.id, rt.phone_id, rt.user_id, rt.end_time, ph.number
      from rent rt join phones ph on ph.id = rt.phone_id
     where rt.status = 'rent' and rt.end_time <= now()
     order by rt.end_time
     for update of rt skip locked
  loop
    v_res := rpc_rent_end(r.phone_id, null, 'expired', v_keep);
    if (v_res->>'ok')::boolean then
      update rent set end_notified_at = now() where id = r.id;
      perform _rent_notify(r.user_id, 'expired', r.number, r.end_time);
      v_ended := v_ended || jsonb_build_object('phone_id', r.phone_id, 'user_id', r.user_id, 'number', r.number);
    end if;
  end loop;

  return jsonb_build_object('warned', v_warned, 'expired', v_ended);
end;
$$;


-- תאימות: RentDevice.py קורא לזה. עכשיו דרך ה-sweep (כולל התראות), אותה תשובה: מערך phone_id
create or replace function rpc_rent_expire(p_keep_templates text[] default '{}')
returns jsonb
language plpgsql
as $$
  declare v jsonb;
  begin
    v := rpc_rent_sweep(nullif(p_keep_templates, '{}'));
    return coalesce((select jsonb_agg(e->'phone_id') from jsonb_array_elements(v->'expired') e), '[]'::jsonb);
  end;
$$;


do $$
declare f text;
begin
  foreach f in array array[
    'rpc_rent_sweep(text[])', 'rpc_rent_expire(text[])', '_rent_notify(uuid,text,text,timestamptz)'
  ] loop
    execute format('revoke all on function %s from public, anon, authenticated', f);
    execute format('grant execute on function %s to service_role', f);
  end loop;
end $$;
