import { Add, Archive, Block, CheckCircle, Edit } from "@mui/icons-material";
import {
  Alert,
  Button,
  Checkbox,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControlLabel,
  IconButton,
  InputLabel,
  MenuItem,
  Select,
  Stack,
  TableCell,
  TableRow,
  Tooltip,
} from "@mui/material";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { api } from "../../api/api";
import type { Department, Employee, Location, User } from "../../api/types";
import {
  ConfirmDialog,
  DataTable,
  ErrorState,
  LoadingState,
  PageHeader,
  StatusChip,
} from "../../components/Ui";
import { formatDateTime } from "../../shared/formatters";
import { label } from "../../shared/labels";
import { queryKeys } from "../../shared/queryKeys";
import { usePagedList } from '../../components/usePagedList';

import type { ZodTypeAny } from "zod";
import { ValidatedForm, ValidatedTextField as TextField, ValidatedControl as FormControl } from "../../components/ValidatedForm";
import { departmentSchema, locationSchema, employeeSchema, userSchema } from "../../shared/validation";

function Editor({
  open,
  title,
  children,
  busy,
  schema,
  value,
  error,
  onClose,
  onSave,
}: {
  open: boolean;
  title: string;
  children: React.ReactNode;
  busy?: boolean;
  schema: ZodTypeAny;
  value: unknown;
  error?: Error | null;
  onClose: () => void;
  onSave: () => void | Promise<unknown>;
}) {
  return (
    <Dialog open={open} onClose={busy ? undefined : onClose} fullWidth maxWidth="sm">
      <ValidatedForm schema={schema} value={value} onValid={onSave} busy={busy}>
      <DialogTitle>{title}</DialogTitle>
      <DialogContent>
        <Stack spacing={2} pt={1}>
          {error && <Alert severity="error">{error.message}</Alert>}
          {children}
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose} disabled={busy}>Отмена</Button>
        <Button variant="contained" disabled={busy} type="submit">
          {busy ? "Сохранение…" : "Сохранить"}
        </Button>
      </DialogActions>
      </ValidatedForm>
    </Dialog>
  );
}

export function DepartmentsPage() {
  const qc = useQueryClient();
  const q = usePagedList<Department>(queryKeys.departments, 'departments', { includeArchived: true });
  const [edit, setEdit] = useState<Partial<Department> | null>(null);
  const [archive, setArchive] = useState<string | null>(null);
  const save = useMutation({
    mutationFn: () =>
      edit?.id
        ? api.updateDepartment(edit.id, {
            code: edit.code!,
            name: edit.name!,
            isInventoryDepartment: !!edit.isInventoryDepartment,
          })
        : api.createDepartment({
            code: edit!.code!,
            name: edit!.name!,
            isInventoryDepartment: !!edit!.isInventoryDepartment,
          }),
    onSuccess: () => {
      setEdit(null);
      void qc.invalidateQueries({ queryKey: queryKeys.departments });
    },
  });
  const del = useMutation({
    mutationFn: api.archiveDepartment,
    onSuccess: () => {
      setArchive(null);
      void qc.invalidateQueries({ queryKey: queryKeys.departments });
    },
  });
  return (
    <>
      <PageHeader
        title="Подразделения"
        action={
          <Button
            variant="contained"
            startIcon={<Add />}
            onClick={() =>
              setEdit({ code: "", name: "", isInventoryDepartment: false })
            }
          >
            Добавить
          </Button>
        }
      />
      {q.controls}
      {q.isLoading ? (
        <LoadingState />
      ) : q.error ? (
        <ErrorState error={q.error} retry={() => void q.refetch()} />
      ) : (
        <DataTable
          headers={[
            "Код",
            "Название",
            "Отдел инвентаризации",
            "Статус",
            "Действия",
          ]}
          empty={!q.data?.length}
        >
          {q.data?.map((x) => (
            <TableRow key={x.id}>
              <TableCell>{x.code}</TableCell>
              <TableCell>{x.name}</TableCell>
              <TableCell>{x.isInventoryDepartment ? "Да" : "Нет"}</TableCell>
              <TableCell>
                {x.isArchived ? <StatusChip value="Archived" /> : "Активно"}
              </TableCell>
              <TableCell>
                <IconButton aria-label="Редактировать" disabled={x.isArchived} onClick={() => setEdit(x)}>
                  <Edit />
                </IconButton>
                {!x.isArchived && (
                  <IconButton aria-label="Архивировать" color="error" onClick={() => setArchive(x.id)}>
                    <Archive />
                  </IconButton>
                )}
              </TableCell>
            </TableRow>
          ))}
        </DataTable>
      )}
      <Editor
        open={!!edit}
        title={
          edit?.id ? "Редактирование подразделения" : "Новое подразделение"
        }
        busy={save.isPending}
        schema={departmentSchema} value={edit} error={save.error}
        onClose={() => { save.reset(); setEdit(null); }}
        onSave={() => save.mutateAsync()}
      >
        <TextField name="code"
          required
          label="Код"
          value={edit?.code ?? ""}
          onChange={(e) => setEdit((v) => ({ ...v!, code: e.target.value }))}
        />
        <TextField name="name"
          required
          label="Название"
          value={edit?.name ?? ""}
          onChange={(e) => setEdit((v) => ({ ...v!, name: e.target.value }))}
        />
        <FormControlLabel
          control={
            <Checkbox
              checked={!!edit?.isInventoryDepartment}
              onChange={(e) =>
                setEdit((v) => ({
                  ...v!,
                  isInventoryDepartment: e.target.checked,
                }))
              }
            />
          }
          label="Отдел инвентаризации"
        />
      </Editor>
      <ConfirmDialog
        open={!!archive}
        title="Архивировать подразделение?"
        text="Связанные данные останутся в системе."
        busy={del.isPending}
        error={del.error}
        onCancel={() => { del.reset(); setArchive(null); }}
        onConfirm={() => archive && del.mutate(archive)}
      />
    </>
  );
}

export function LocationsPage() {
  const qc = useQueryClient();
  const [dep, setDep] = useState("");
  const q = usePagedList<Location>(['locations'], 'locations', { departmentId: dep || undefined, includeArchived: true });
  const deps = useQuery({
    queryKey: queryKeys.departments,
    queryFn: () => api.departments(),
  });
  const [edit, setEdit] = useState<Partial<Location> | null>(null);
  const [archive, setArchive] = useState<string | null>(null);
  const save = useMutation({
    mutationFn: () => {
      const body = {
        code: edit!.code!,
        name: edit!.name!,
        departmentId: edit!.departmentId!,
      };
      return edit?.id
        ? api.updateLocation(edit.id, body)
        : api.createLocation(body);
    },
    onSuccess: () => {
      setEdit(null);
      void qc.invalidateQueries({ queryKey: ["locations"] });
    },
  });
  const del = useMutation({
    mutationFn: api.archiveLocation,
    onSuccess: () => {
      setArchive(null);
      void qc.invalidateQueries({ queryKey: ["locations"] });
    },
  });
  return (
    <>
      <PageHeader
        title="Помещения"
        action={
          <Button
            variant="contained"
            startIcon={<Add />}
            onClick={() => setEdit({ code: "", name: "", departmentId: dep })}
          >
            Добавить
          </Button>
        }
      />
      <FormControl name="departmentId" size="small" sx={{ minWidth: 260, mb: 2 }}>
          <InputLabel>Подразделение</InputLabel>
        <Select
          label="Подразделение"
          value={dep}
          onChange={(e) => setDep(e.target.value)}
        >
          <MenuItem value="">Все</MenuItem>
          {deps.data?.map((x) => (
            <MenuItem key={x.id} value={x.id}>
              {x.name}
            </MenuItem>
          ))}
        </Select>
      </FormControl>
      {q.controls}
      {q.isLoading ? (
        <LoadingState />
      ) : q.error ? (
        <ErrorState error={q.error} retry={() => void q.refetch()} />
      ) : (
        <DataTable
          headers={["Код", "Название", "Подразделение", "Статус", "Действия"]}
          empty={!q.data?.length}
        >
          {q.data?.map((x) => (
            <TableRow key={x.id}>
              <TableCell>{x.code}</TableCell>
              <TableCell>{x.name}</TableCell>
              <TableCell>{x.departmentName}</TableCell>
              <TableCell>
                {x.isArchived ? <StatusChip value="Archived" /> : "Активно"}
              </TableCell>
              <TableCell>
                <IconButton aria-label="Редактировать" disabled={x.isArchived} onClick={() => setEdit(x)}>
                  <Edit />
                </IconButton>
                {!x.isArchived && (
                  <IconButton aria-label="Архивировать" color="error" onClick={() => setArchive(x.id)}>
                    <Archive />
                  </IconButton>
                )}
              </TableCell>
            </TableRow>
          ))}
        </DataTable>
      )}
      <Editor
        open={!!edit}
        title={edit?.id ? "Редактирование помещения" : "Новое помещение"}
        busy={save.isPending}
        schema={locationSchema} value={edit} error={save.error}
        onClose={() => { save.reset(); setEdit(null); }}
        onSave={() => save.mutateAsync()}
      >
        <TextField name="code"
          required
          label="Код"
          value={edit?.code ?? ""}
          onChange={(e) => setEdit((v) => ({ ...v!, code: e.target.value }))}
        />
        <TextField name="name"
          required
          label="Название"
          value={edit?.name ?? ""}
          onChange={(e) => setEdit((v) => ({ ...v!, name: e.target.value }))}
        />
        <FormControl name="departmentId" required>
          <InputLabel>Подразделение</InputLabel>
          <Select
            label="Подразделение"
            value={edit?.departmentId ?? ""}
            onChange={(e) =>
              setEdit((v) => ({ ...v!, departmentId: e.target.value }))
            }
          >
            {deps.data?.map((x) => (
              <MenuItem key={x.id} value={x.id}>
                {x.name}
              </MenuItem>
            ))}
          </Select>
        </FormControl>
      </Editor>
      <ConfirmDialog
        open={!!archive}
        title="Архивировать помещение?"
        text="Помещение исчезнет из активного справочника."
        busy={del.isPending}
        error={del.error}
        onCancel={() => { del.reset(); setArchive(null); }}
        onConfirm={() => archive && del.mutate(archive)}
      />
    </>
  );
}

export function EmployeesPage() {
  const qc = useQueryClient();
  const q = usePagedList<Employee>(queryKeys.employees, 'employees', { includeArchived: true });
  const deps = useQuery({
    queryKey: queryKeys.departments,
    queryFn: () => api.departments(),
  });
  const [edit, setEdit] = useState<Partial<Employee> | null>(null);
  const [archive, setArchive] = useState<string | null>(null);
  const body = () => ({
    personnelNumber: edit!.personnelNumber!,
    fullName: edit!.fullName!,
    departmentId: edit!.departmentId!,
    isMateriallyResponsible: !!edit!.isMateriallyResponsible,
    isEmployed: !!edit!.isEmployed,
  });
  const save = useMutation({
    mutationFn: () =>
      edit?.id
        ? api.updateEmployee(edit.id, body())
        : api.createEmployee(body()),
    onSuccess: () => {
      setEdit(null);
      void qc.invalidateQueries({ queryKey: queryKeys.employees });
    },
  });
  const del = useMutation({
    mutationFn: api.archiveEmployee,
    onSuccess: () => {
      setArchive(null);
      void qc.invalidateQueries({ queryKey: queryKeys.employees });
    },
  });
  return (
    <>
      <PageHeader
        title="Сотрудники"
        action={
          <Button
            variant="contained"
            startIcon={<Add />}
            onClick={() =>
              setEdit({
                personnelNumber: "",
                fullName: "",
                departmentId: "",
                isEmployed: true,
                isMateriallyResponsible: false,
              })
            }
          >
            Добавить
          </Button>
        }
      />
      {q.controls}
      {q.isLoading ? (
        <LoadingState />
      ) : q.error ? (
        <ErrorState error={q.error} retry={() => void q.refetch()} />
      ) : (
        <DataTable
          headers={[
            "Табельный №",
            "ФИО",
            "Подразделение",
            "МОЛ",
            "Работает",
            "Статус",
            "Действия",
          ]}
          empty={!q.data?.length}
        >
          {q.data?.map((x) => (
            <TableRow key={x.id}>
              <TableCell>{x.personnelNumber}</TableCell>
              <TableCell>{x.fullName}</TableCell>
              <TableCell>{x.departmentName}</TableCell>
              <TableCell>{x.isMateriallyResponsible ? "Да" : "Нет"}</TableCell>
              <TableCell>{x.isEmployed ? "Да" : "Нет"}</TableCell>
              <TableCell>
                {x.isArchived ? <StatusChip value="Archived" /> : "Активно"}
              </TableCell>
              <TableCell>
                <IconButton aria-label="Редактировать" disabled={x.isArchived} onClick={() => setEdit(x)}>
                  <Edit />
                </IconButton>
                {!x.isArchived && (
                  <IconButton aria-label="Архивировать" color="error" onClick={() => setArchive(x.id)}>
                    <Archive />
                  </IconButton>
                )}
              </TableCell>
            </TableRow>
          ))}
        </DataTable>
      )}
      <Editor
        open={!!edit}
        title={edit?.id ? "Редактирование сотрудника" : "Новый сотрудник"}
        busy={save.isPending}
        schema={employeeSchema} value={edit} error={save.error}
        onClose={() => { save.reset(); setEdit(null); }}
        onSave={() => save.mutateAsync()}
      >
        <TextField name="personnelNumber"
          required
          label="Табельный номер"
          value={edit?.personnelNumber ?? ""}
          onChange={(e) =>
            setEdit((v) => ({ ...v!, personnelNumber: e.target.value }))
          }
        />
        <TextField name="fullName"
          required
          label="ФИО"
          value={edit?.fullName ?? ""}
          onChange={(e) =>
            setEdit((v) => ({ ...v!, fullName: e.target.value }))
          }
        />
        <FormControl name="departmentId" required>
          <InputLabel>Подразделение</InputLabel>
          <Select
            label="Подразделение"
            value={edit?.departmentId ?? ""}
            onChange={(e) =>
              setEdit((v) => ({ ...v!, departmentId: e.target.value }))
            }
          >
            {deps.data?.map((x) => (
              <MenuItem key={x.id} value={x.id}>
                {x.name}
              </MenuItem>
            ))}
          </Select>
        </FormControl>
        <FormControlLabel
          control={
            <Checkbox
              checked={!!edit?.isMateriallyResponsible}
              onChange={(e) =>
                setEdit((v) => ({
                  ...v!,
                  isMateriallyResponsible: e.target.checked,
                }))
              }
            />
          }
          label="Материально ответственное лицо"
        />
        <FormControlLabel
          control={
            <Checkbox
              checked={!!edit?.isEmployed}
              onChange={(e) =>
                setEdit((v) => ({ ...v!, isEmployed: e.target.checked }))
              }
            />
          }
          label="Работает"
        />
      </Editor>
      <ConfirmDialog
        open={!!archive}
        title="Архивировать сотрудника?"
        text="Учетная история сотрудника сохранится."
        busy={del.isPending}
        error={del.error}
        onCancel={() => { del.reset(); setArchive(null); }}
        onConfirm={() => archive && del.mutate(archive)}
      />
    </>
  );
}

const emptyUser = { login: "", password: "", confirm: "", role: "InventoryEmployee", employeeId: "" };

export function UsersPage() {
  const qc = useQueryClient();
  const departments = useQuery({ queryKey: queryKeys.departments, queryFn: () => api.departments() });
  const q = usePagedList<User>(queryKeys.users, 'users');
  const emps = useQuery({
    queryKey: queryKeys.employees,
    queryFn: () => api.employees(),
  });
  const [edit, setEdit] = useState<typeof emptyUser | null>(null);
  const [target, setTarget] = useState<User | null>(null);
  const employeesForRole = (role: string) => (emps.data ?? []).filter(employee =>
    employee.isEmployed && !employee.isArchived && departments.data?.some(department =>
      department.id === employee.departmentId && !department.isArchived &&
      (role === "Administrator" || department.isInventoryDepartment)));
  const eligibleEmployees = employeesForRole(edit?.role ?? emptyUser.role);
  const create = useMutation({
    mutationFn: () =>
      api.createUser({
        login: edit!.login,
        password: edit!.password,
        role: edit!.role,
        employeeId: edit!.employeeId || null,
      }),
    onSuccess: () => {
      setEdit(null);
      void qc.invalidateQueries({ queryKey: queryKeys.users });
    },
  });
  const block = useMutation({
    mutationFn: () => api.setUserBlocked(target!.id, !target!.isBlocked),
    onSuccess: () => {
      setTarget(null);
      void qc.invalidateQueries({ queryKey: queryKeys.users });
    },
  });
  return (
    <>
      <PageHeader
        title="Пользователи"
        action={
          <Button
            variant="contained"
            startIcon={<Add />}
            onClick={() => setEdit(emptyUser)}
          >
            Создать пользователя
          </Button>
        }
      />
      {q.controls}
      {q.isLoading ? (
        <LoadingState />
      ) : q.error ? (
        <ErrorState error={q.error} retry={() => void q.refetch()} />
      ) : (
        <DataTable
          headers={[
            "Логин",
            "Роль",
            "Сотрудник",
            "Статус",
            "Создан",
            "Действия",
          ]}
          empty={!q.data?.length}
        >
          {q.data?.map((x) => (
            <TableRow key={x.id}>
              <TableCell>{x.login}</TableCell>
              <TableCell>{label(x.role)}</TableCell>
              <TableCell>{x.employeeName ?? "—"}</TableCell>
              <TableCell>
                {x.isBlocked ? <StatusChip value="Blocked" /> : "Активен"}
              </TableCell>
              <TableCell>{formatDateTime(x.createdAt)}</TableCell>
              <TableCell>
                <Tooltip
                  title={x.isBlocked ? "Разблокировать" : "Заблокировать"}
                >
                  <IconButton
                    color={x.isBlocked ? "success" : "error"}
                    onClick={() => setTarget(x)}
                  >
                    {x.isBlocked ? <CheckCircle /> : <Block />}
                  </IconButton>
                </Tooltip>
              </TableCell>
            </TableRow>
          ))}
        </DataTable>
      )}
      <Editor
        open={!!edit}
        title="Новый пользователь"
        busy={create.isPending}
        schema={userSchema} value={edit} error={create.error}
        onClose={() => { create.reset(); setEdit(null); }}
        onSave={() => create.mutateAsync()}
      >
        <TextField name="login"
          required
          label="Логин"
          value={edit?.login ?? ""}
          onChange={(e) => setEdit((v) => ({ ...v!, login: e.target.value }))}
        />
        <TextField name="password"
          required
          type="password"
          label="Пароль (не менее 8 символов)"
          value={edit?.password ?? ""}
          onChange={(e) => setEdit((v) => ({ ...v!, password: e.target.value }))}
        />
        <TextField name="confirm"
          required
          type="password"
          label="Подтверждение пароля"
          error={!!edit?.confirm && edit.confirm !== edit.password}
          value={edit?.confirm ?? ""}
          onChange={(e) => setEdit((v) => ({ ...v!, confirm: e.target.value }))}
        />
        <FormControl name="role">
          <InputLabel>Роль</InputLabel>
          <Select
            label="Роль"
            value={edit?.role ?? emptyUser.role}
            onChange={(e) => {
              const role = e.target.value;
              const allowed = employeesForRole(role);
              setEdit(v => v && { ...v, role, employeeId: allowed.some(employee => employee.id === v.employeeId) ? v.employeeId : "" });
            }}
          >
            <MenuItem value="Administrator">Администратор</MenuItem>
            <MenuItem value="InventoryEmployee">Инвентаризатор</MenuItem>
          </Select>
        </FormControl>
        <FormControl name="employeeId">
          <InputLabel>Сотрудник</InputLabel>
          <Select
            label="Сотрудник"
            value={edit?.employeeId ?? ""}
            onChange={(e) => setEdit((v) => ({ ...v!, employeeId: e.target.value }))}
          >
            <MenuItem value="">Не привязан</MenuItem>
            {eligibleEmployees.map((x) => (
              <MenuItem key={x.id} value={x.id}>
                {x.fullName}
              </MenuItem>
            ))}
          </Select>
        </FormControl>
      </Editor>
      <ConfirmDialog
        open={!!target}
        title={
          target?.isBlocked
            ? "Разблокировать пользователя?"
            : "Заблокировать пользователя?"
        }
        text={target?.login ?? ""}
        busy={block.isPending}
        error={block.error}
        onCancel={() => { block.reset(); setTarget(null); }}
        onConfirm={() => block.mutate()}
      />
    </>
  );
}
