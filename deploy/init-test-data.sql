-- LOCAL MVP ONLY. Apply AFTER EF Core migrations to the existing database.
-- Additive and transactional: no DELETE/TRUNCATE/UPDATE, no changes to real users.
-- Repeat runs preserve edits/scans/password changes and do not duplicate fixtures.
-- Reserved keys: MVP-*, mvp.worker1..3, INV-MVP-*.
-- Public test worker password: Manual-MVP-2026! (ASP.NET Identity PBKDF2 hash).
-- Never load public test credentials into a production/shared database.
BEGIN;
SELECT pg_advisory_xact_lock(hashtext('asset-inventory.local-mvp-fixture.v1'));
CREATE OR REPLACE FUNCTION pg_temp.mvp_id(kind text, n integer) RETURNS uuid
LANGUAGE sql IMMUTABLE AS $$
  SELECT (substr(stamp,1,8) || '-' || substr(stamp,9,4) || '-7' || substr(hash,1,3)
    || '-8' || substr(hash,4,3) || '-' || substr(hash,7,12))::uuid
  FROM (SELECT lpad(to_hex((extract(epoch FROM '2026-10-04T09:00:00Z'::timestamptz)*1000)::bigint),12,'0') stamp,
    md5('asset-inventory.local-mvp-fixture.v1:' || kind || ':' || n::text) hash) value;
$$;

INSERT INTO departments (id, code, name, is_inventory_department, is_archived)
SELECT pg_temp.mvp_id('department', n), 'MVP-DEP-' || n,
  (ARRAY['Бухгалтерия (тест)','IT-отдел (тест)','Склад (тест)','Производство (тест)','Администрация (тест)','Инвентаризаторы (тест)'])[n], n=6, false
FROM generate_series(1,6) n ON CONFLICT (id) DO NOTHING;

INSERT INTO locations (id, code, name, department_id, is_archived)
SELECT pg_temp.mvp_id('location', n), 'MVP-ROOM-' || n,
  'Тестовое помещение ' || n, pg_temp.mvp_id('department', (n-1)/2+1), false
FROM generate_series(1,10) n ON CONFLICT (id) DO NOTHING;

INSERT INTO employees (id, personnel_number, full_name, department_id, is_materially_responsible, is_employed, is_archived)
SELECT pg_temp.mvp_id('employee', n), 'MVP-EMP-' || n,
  (ARRAY['Петрова Анна Сергеевна (тест)','Иванов Павел Олегович (тест)','Сидоров Иван Петрович (тест)','Смирнова Ольга Андреевна (тест)','Кузнецов Алексей Игоревич (тест)',
    'Тестовый инвентаризатор 1','Тестовый инвентаризатор 2','Тестовый инвентаризатор 3'])[n],
  pg_temp.mvp_id('department', least(n,6)), n<=5, true, false
FROM generate_series(1,8) n ON CONFLICT (id) DO NOTHING;

INSERT INTO user_accounts (id, login, normalized_login, password_hash, role, employee_id, is_blocked, created_at)
SELECT pg_temp.mvp_id('user', n), 'mvp.worker' || n, 'MVP.WORKER' || n,
  'AQAAAAIAAYagAAAAEB3X5ObOU2j6xsOx7+NwgsP7i7SyQRxFSKcOZHnnMeZgwe/PK/NajUCZf7Zy+U1qbQ==',
  'InventoryEmployee', pg_temp.mvp_id('employee', n+5), false, '2026-10-04T09:00:00Z'::timestamptz
FROM generate_series(1,3) n ON CONFLICT (id) DO NOTHING;

INSERT INTO assets (id, inventory_number, name, accounting_account, serial_number, manufacture_year, accepted_at, initial_cost, department_id, location_id, materially_responsible_employee_id, status, archived_at, version)
SELECT pg_temp.mvp_id('asset', n), 'MVP-' || lpad(n::text,4,'0'),
  (ARRAY['Ноутбук','Монитор','Принтер','Стол офисный','Сканер','Шкаф','Сервер','Станок','Стеллаж','Проектор'])[(n-1)%10+1] || ' — тестовый ' || n,
  '01.01', 'SN-MVP-' || lpad(n::text,4,'0'), 2020+(n-1)%6, '2026-01-15'::date, 3500+(n-1)*1250,
  pg_temp.mvp_id('department',(n-1)%5+1), pg_temp.mvp_id('location',((n-1)%5)*2+1+(n/5)%2),
  pg_temp.mvp_id('employee',(n-1)%5+1), 'InUse', NULL, 1
FROM generate_series(1,1000) n ON CONFLICT (id) DO NOTHING;

INSERT INTO asset_assignment_history (id, asset_id, department_id, location_id, materially_responsible_employee_id, valid_from, valid_to)
SELECT pg_temp.mvp_id('assignment', n), a.id, a.department_id, a.location_id, a.materially_responsible_employee_id, '2026-01-15T09:00:00Z'::timestamptz, NULL
FROM generate_series(1,1000) n JOIN assets a ON a.id=pg_temp.mvp_id('asset',n)
ON CONFLICT (id) DO NOTHING;

INSERT INTO inventory_schedules (id, name, interval_months, next_run_at, launch_mode, is_enabled, last_run_at)
VALUES
  (pg_temp.mvp_id('schedule',1),'MVP: ежегодная проверка',12,'2027-01-15T09:00:00Z','CreateDraft',false,NULL),
  (pg_temp.mvp_id('schedule',2),'MVP: ежеквартальная проверка',3,'2027-02-01T09:00:00Z','AutoStart',false,NULL)
ON CONFLICT (id) DO NOTHING;

-- Drafts can be started normally by the existing administrator. Schedules are
-- disabled to avoid autonomous test writes.
INSERT INTO inventories (id, number, type, reason, reason_details, status, target_employee_id, planned_at, started_at, scanning_finished_at, completed_at, created_by_user_id, created_at, scheduled_period, source_schedule_id, report_context_json, source_inventory_id)
SELECT pg_temp.mvp_id('inventory',n), 'INV-MVP-' || lpad(n::text,3,'0'),
  CASE WHEN n=2 THEN 'EventDriven' ELSE 'Manual' END, 'Other', 'Синтетический сценарий MVP ' || n,
  CASE WHEN n=4 THEN 'Cancelled' ELSE 'Draft' END,
  CASE WHEN n=2 THEN pg_temp.mvp_id('employee',1) ELSE NULL END,
  '2026-10-10T09:00:00Z'::timestamptz, NULL, NULL, NULL, NULL, '2026-10-04T09:00:00Z'::timestamptz, NULL, NULL, NULL, NULL
FROM generate_series(1,4) n ON CONFLICT (id) DO NOTHING;

INSERT INTO inventory_assignments (id, inventory_id, user_id, role, confirmation_status, confirmed_at, rejection_reason)
SELECT pg_temp.mvp_id('commission',n), pg_temp.mvp_id('inventory',n), pg_temp.mvp_id('user',(n-1)%3+1), 'Chairperson', 'Pending', NULL, NULL
FROM generate_series(1,4) n ON CONFLICT (id) DO NOTHING;

-- Only newly inserted checks receive an assignment and snapshot. A rerun must
-- preserve completed checks, scans and any changes to the original scope.
WITH scenarios AS (
  SELECT n, label, status, with_discrepancies,
    pg_temp.mvp_id('employee', mol) AS mol_id, pg_temp.mvp_id('user', worker) AS user_id
  FROM (VALUES
    (5, 'Демо: активный обход с повреждённым ОС', 'Active', 2, 1, true),
    (6, 'Демо: обход с недостачей ожидает подтверждения', 'AwaitingConfirmation', 3, 2, true),
    (7, 'Демо: завершённая инвентаризация без расхождений', 'Completed', 4, 3, false),
    (8, 'Демо: завершённая инвентаризация с недостачей и повреждением', 'Completed', 5, 1, true)
  ) s(n, label, status, mol, worker, with_discrepancies)
), eligible_assets AS (
  SELECT n AS asset_no, a.*
  FROM generate_series(1,1000) n JOIN assets a ON a.id=pg_temp.mvp_id('asset',n)
  WHERE a.archived_at IS NULL AND a.status <> 'Archived'
), new_inventories AS (
  INSERT INTO inventories (id, number, type, reason, reason_details, status, target_employee_id, planned_at,
    started_at, scanning_finished_at, completed_at, round, created_at)
  SELECT pg_temp.mvp_id('inventory', s.n), 'INV-MVP-' || lpad(s.n::text,3,'0'), 'EventDriven', 'Other',
    s.label, s.status, s.mol_id, '2026-10-10T09:00:00Z', '2026-10-10T09:00:00Z',
    CASE WHEN s.status<>'Active' THEN '2026-10-10T09:15:00Z'::timestamptz END,
    CASE WHEN s.status='Completed' THEN '2026-10-10T09:17:00Z'::timestamptz END,
    1, '2026-10-04T09:00:00Z'
  FROM scenarios s
  JOIN user_accounts u ON u.id=s.user_id
  JOIN employees e ON e.id=u.employee_id
  JOIN departments d ON d.id=e.department_id
  WHERE u.role='InventoryEmployee' AND NOT u.is_blocked AND e.is_employed AND NOT e.is_archived
    AND d.is_inventory_department AND NOT d.is_archived AND e.id<>s.mol_id
    AND (SELECT count(*) FROM eligible_assets a WHERE a.materially_responsible_employee_id=s.mol_id) >= 3
  ON CONFLICT (id) DO NOTHING
  RETURNING id, status, started_at, completed_at
), new_assignments AS (
  INSERT INTO inventory_assignments (id, inventory_id, user_id, role, confirmation_status, confirmed_at)
  SELECT pg_temp.mvp_id('commission', s.n), i.id, s.user_id, 'Chairperson',
    CASE WHEN i.completed_at IS NOT NULL THEN 'Confirmed' ELSE 'Pending' END, i.completed_at
  FROM new_inventories i JOIN scenarios s ON i.id=pg_temp.mvp_id('inventory',s.n)
  RETURNING inventory_id, user_id
), snapshot AS (
  SELECT i.id AS inventory_id, i.started_at, i.status='Active' AS active, s.n AS scenario_no, s.with_discrepancies,
    m.user_id, a.*,
    row_number() OVER (PARTITION BY i.id ORDER BY a.asset_no) AS position
  FROM new_inventories i
  JOIN new_assignments m ON m.inventory_id=i.id
  JOIN scenarios s ON i.id=pg_temp.mvp_id('inventory',s.n)
  JOIN eligible_assets a ON a.materially_responsible_employee_id=s.mol_id
), observations AS (
  SELECT s.*,
    CASE
      WHEN s.active AND s.position>2 THEN 'Pending'
      WHEN NOT s.active AND s.with_discrepancies AND s.position=3 THEN 'NotFound'
      ELSE 'Found'
    END AS result,
    s.with_discrepancies AND s.position=2 AS damaged
  FROM snapshot s
)
INSERT INTO inventory_asset_snapshots (id, inventory_id, asset_id, inventory_number, name, accounting_account,
  serial_number, manufacture_year, accepted_at, initial_cost, department_id, location_id, materially_responsible_employee_id,
  department_name, location_name, materially_responsible_full_name, result, condition, comment, scanned_at, scanned_by_user_id)
SELECT pg_temp.mvp_id('inventory-item-' || a.scenario_no, a.asset_no), a.inventory_id, a.id, a.inventory_number, a.name,
  a.accounting_account, a.serial_number, a.manufacture_year, a.accepted_at, a.initial_cost, a.department_id, a.location_id,
  a.materially_responsible_employee_id, d.name, l.name, mol.full_name,
  a.result,
  CASE WHEN a.damaged THEN 'Damaged' WHEN a.result='Found' THEN 'Serviceable' END,
  CASE WHEN a.damaged THEN 'Тестовое замечание: повреждён корпус.' END,
  CASE WHEN a.result='Found' THEN a.started_at + interval '1 minute' END,
  CASE WHEN a.result='Found' THEN a.user_id END
FROM observations a
JOIN departments d ON d.id=a.department_id
JOIN locations l ON l.id=a.location_id
JOIN employees mol ON mol.id=a.materially_responsible_employee_id;

INSERT INTO audit_logs (id, user_id, action, entity_type, entity_id, data_json, ip_address, correlation_id, created_at)
VALUES (pg_temp.mvp_id('audit',1),NULL,'test-fixture.initialized','LocalMvpFixture','v1',
  '{"synthetic":true,"assets":1000,"departments":6,"locations":10,"employees":8,"workers":3,"inventories":4,"schedules":2}'::jsonb,NULL,'local-mvp-fixture-v1',now())
ON CONFLICT (id) DO NOTHING;
INSERT INTO audit_logs (id, user_id, action, entity_type, entity_id, data_json, ip_address, correlation_id, created_at)
VALUES (pg_temp.mvp_id('audit',2),NULL,'test-fixture.expanded','LocalMvpFixture','v1-1000',
  '{"synthetic":true,"targetAssets":1000}'::jsonb,NULL,'local-mvp-fixture-v1-1000',now())
ON CONFLICT (id) DO NOTHING;

-- Use the same singleton lock as the organization editor.
LOCK TABLE organizations IN SHARE ROW EXCLUSIVE MODE;
INSERT INTO organizations (id, name, inn, kpp, okpo, address, director_name)
SELECT pg_temp.mvp_id('organization', 1), 'ООО «Тестовое предприятие»',
  '1234567890', '123456789', '12345678', 'Екатеринбург, тестовый адрес', 'Тестовый руководитель'
WHERE NOT EXISTS (SELECT 1 FROM organizations);

COMMIT;
