-- 14_mail_rent_release.sql
-- When a phone's owner changes (rental ended → user_id = system, or a new rental), the
-- old owner's mails must not stay on it. rpc_mail_release_stale() finds every mailbox whose
-- user_id differs from the phone's current user_id, deletes that phone's mail_inbound rows
-- and the mailbox row. MailboxProvisioner calls it at the start of each sweep and then
-- re-provisions the phone for the new owner in the same sweep (same address, the Cloudflare
-- rule is reused, so nothing changes in Cloudflare).
-- Safe with more than one caller: rows are claimed with FOR UPDATE SKIP LOCKED.

create or replace function public.rpc_mail_release_stale()
returns jsonb
language plpgsql
security definer
set search_path = public
as $$
declare
  r     record;
  n     int;
  out_a jsonb := '[]'::jsonb;
begin
  for r in
    select m.id, m.address, m.phone_id, m.user_id as old_user, p.user_id as new_user
    from public.mailboxes m
    join public.phones p on p.id = m.phone_id
    where p.user_id is distinct from m.user_id
    for update of m skip locked
  loop
    delete from public.mail_inbound where phone_id = r.phone_id;
    get diagnostics n = row_count;
    delete from public.mailboxes where id = r.id;

    out_a := out_a || jsonb_build_object(
      'address',  r.address,
      'phone_id', r.phone_id,
      'old_user', r.old_user,
      'new_user', r.new_user,
      'mails',    n);
  end loop;

  return jsonb_build_object('released', out_a);
end;
$$;

revoke all on function public.rpc_mail_release_stale() from public, anon, authenticated;
grant execute on function public.rpc_mail_release_stale() to service_role;
