-- ════════════════════════════════════════════════════════════════════════════
-- 07_rent_fk_rls.sql — rent.user_id → users + RLS בסגנון "own phones" (user_id = auth.uid())
-- ה-backend קורא דרך service_role (עוקף RLS). ה-policies שומרות על client mode / גישה ישירה.
-- device / sim — בלי policy: service_role בלבד.
-- ════════════════════════════════════════════════════════════════════════════

do $$
begin
  if not exists (select 1 from pg_constraint where conname = 'rent_user_id_fkey') then
    alter table rent add constraint rent_user_id_fkey foreign key (user_id) references users(id);
  end if;
end $$;

drop policy if exists "own rents" on rent;
create policy "own rents" on rent
  for select
  using (user_id = auth.uid());

drop policy if exists "own phone sms" on sms;
create policy "own phone sms" on sms
  for select
  using (exists (select 1 from phones p where p.id = sms.phone_id and p.user_id = auth.uid()));
