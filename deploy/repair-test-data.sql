-- One-time, repeat-safe correction of the former LOCAL MVP fixture IDs.
-- Only IDs from our exact v1 namespace are remapped. Back up before applying.
BEGIN;
SELECT pg_advisory_xact_lock(hashtext('asset-inventory.local-mvp-fixture.v1'));
CREATE TEMP TABLE mvp_id_map(old_id uuid PRIMARY KEY, new_id uuid UNIQUE) ON COMMIT DROP;
DO $$
DECLARE spec record; n integer; h text; stamp text; previous uuid; replacement uuid;
BEGIN
  stamp := lpad(to_hex((extract(epoch FROM '2026-10-04T09:00:00Z'::timestamptz)*1000)::bigint),12,'0');
  FOR spec IN SELECT * FROM (VALUES
    ('department','departments',6),('location','locations',10),('employee','employees',8),
    ('user','user_accounts',3),('asset','assets',1000),('assignment','asset_assignment_history',1000),
    ('schedule','inventory_schedules',2),('inventory','inventories',4),('commission','inventory_assignments',4),('audit','audit_logs',2)
  ) v(kind,tablename,total) LOOP
    FOR n IN 1..spec.total LOOP
      h := md5('asset-inventory.local-mvp-fixture.v1:' || spec.kind || ':' || n::text);
      previous := h::uuid;
      replacement := (substr(stamp,1,8)||'-'||substr(stamp,9,4)||'-7'||substr(h,1,3)||'-8'||substr(h,4,3)||'-'||substr(h,7,12))::uuid;
      EXECUTE format('INSERT INTO mvp_id_map SELECT $1,$2 WHERE EXISTS(SELECT 1 FROM public.%I WHERE id=$1)',spec.tablename) USING previous,replacement;
    END LOOP;
  END LOOP;
END $$;
-- Save and restore FK properties in the SAME transaction. No FK is dropped or
-- disabled; PostgreSQL checks the final relationships before COMMIT.
CREATE TEMP TABLE mvp_fk_original AS
SELECT conrelid::regclass AS tablename,conname FROM pg_constraint
WHERE contype='f' AND NOT condeferrable AND connamespace='public'::regnamespace;
DO $$
DECLARE entry record;
BEGIN
  IF NOT EXISTS(SELECT 1 FROM mvp_id_map) THEN RETURN; END IF;
  FOR entry IN SELECT * FROM mvp_fk_original LOOP
    EXECUTE format('ALTER TABLE %s ALTER CONSTRAINT %I DEFERRABLE INITIALLY DEFERRED',entry.tablename,entry.conname);
  END LOOP;
  FOR entry IN SELECT table_name,column_name FROM information_schema.columns
    WHERE table_schema='public' AND udt_name='uuid' LOOP
    EXECUTE format('UPDATE public.%I t SET %I=m.new_id FROM mvp_id_map m WHERE t.%I=m.old_id',entry.table_name,entry.column_name,entry.column_name);
  END LOOP;
  SET CONSTRAINTS ALL IMMEDIATE;
  FOR entry IN SELECT * FROM mvp_fk_original LOOP
    EXECUTE format('ALTER TABLE %s ALTER CONSTRAINT %I NOT DEFERRABLE',entry.tablename,entry.conname);
  END LOOP;
END $$;
-- Correct only the two former fixture values, preserving user edits/enabled state.
UPDATE inventory_schedules SET launch_mode=CASE launch_mode WHEN 'Manual' THEN 'CreateDraft' ELSE 'AutoStart' END
WHERE name IN ('MVP: ежегодная проверка','MVP: ежеквартальная проверка')
  AND launch_mode IN ('Manual','Automatic');
DROP TABLE mvp_fk_original;
COMMIT;
