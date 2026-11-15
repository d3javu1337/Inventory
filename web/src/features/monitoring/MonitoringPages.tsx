import {
  Add,
  Delete,
  Edit,
  Link as LinkIcon,
  Refresh,
} from "@mui/icons-material";
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
} from "@mui/material";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { Link } from "react-router-dom";
import { api } from "../../api/api";
import type { Schedule, IntegrationEvent } from "../../api/types";
import { usePagedList } from '../../components/usePagedList';
import {
  ConfirmDialog,
  DataTable,
  ErrorState,
  LoadingState,
  PageHeader,
  PrimaryWithId,
  StatusChip,
} from "../../components/Ui";
import { formatDateTime } from "../../shared/formatters";
import { label } from "../../shared/labels";
import { queryKeys } from "../../shared/queryKeys";

import { ValidatedForm, ValidatedTextField as TextField, ValidatedControl as FormControl } from "../../components/ValidatedForm";
import { scheduleSchema, eventSchema, toLocalDateTimeInput, dateTimeToIso } from "../../shared/validation";

export function SchedulesPage() {
  const qc = useQueryClient();
  const q = usePagedList<Schedule>(queryKeys.schedules, 'inventory-schedules', {}, 15_000);
  const [edit, setEdit] = useState<(Pick<Schedule, 'name' | 'intervalMonths' | 'nextRunAt' | 'launchMode' | 'isEnabled'> & Partial<Pick<Schedule, 'id' | 'retryAt'>>) | null>(null);
  const [del, setDel] = useState<string | null>(null);
  const body = () => ({
    name: edit!.name,
    intervalMonths: Number(edit!.intervalMonths),
    nextRunAt: dateTimeToIso(edit!.nextRunAt!),
    launchMode: edit!.launchMode,
    isEnabled: !!edit!.isEnabled,
  });
  const save = useMutation({
    mutationFn: () =>
      edit?.id
        ? api.updateSchedule(edit.id, body())
        : api.createSchedule(body()),
    onSuccess: () => {
      setEdit(null);
      void qc.invalidateQueries({ queryKey: queryKeys.schedules });
    },
  });
  const remove = useMutation({
    mutationFn: api.deleteSchedule,
    onSuccess: () => {
      setDel(null);
      void qc.invalidateQueries({ queryKey: queryKeys.schedules });
    },
  });
  const closeEditor = () => { save.reset(); setEdit(null); };
  return (
    <>
      <PageHeader
        title="Расписания"
        description="Автоматическое создание периодических инвентаризаций"
        action={
          <Button
            variant="contained"
            startIcon={<Add />}
            onClick={() =>
              setEdit({
                name: "",
                intervalMonths: 12,
                nextRunAt: toLocalDateTimeInput(),
                launchMode: "CreateDraft",
                isEnabled: true,
              })
            }
          >
            Создать расписание
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
            "Название",
            "Интервал",
            "Следующий запуск",
            "Режим",
            "Включено",
            "Последний запуск",
            "Действия",
          ]}
          empty={!q.data?.length}
        >
          {q.data?.map((x) => (
            <TableRow key={x.id}>
              <TableCell>{x.name}</TableCell>
              <TableCell>
                {x.intervalMonths} мес.
                {x.dayOfMonth && <div>{x.dayOfMonth}-го числа</div>}
              </TableCell>
              <TableCell>
                {formatDateTime(x.nextRunAt)}
                {x.isEnabled && x.retryAt && (
                  <Stack spacing={0.5} alignItems="flex-start" mt={0.5}>
                    <StatusChip value="Failed" />
                    <span>Неудачных попыток {x.consecutiveFailures}</span>
                    <span>Повтор не раньше {formatDateTime(x.retryAt)}</span>
                  </Stack>
                )}
              </TableCell>
              <TableCell>{label(x.launchMode)}</TableCell>
              <TableCell>{x.isEnabled ? "Да" : "Нет"}</TableCell>
              <TableCell>{formatDateTime(x.lastRunAt)}</TableCell>
              <TableCell>
                <IconButton
                  aria-label="Редактировать"
                  onClick={() =>
                    setEdit({ ...x, nextRunAt: toLocalDateTimeInput(x.nextRunAt) })
                  }
                >
                  <Edit />
                </IconButton>
                <IconButton aria-label="Удалить" color="error" onClick={() => setDel(x.id)}>
                  <Delete />
                </IconButton>
              </TableCell>
            </TableRow>
          ))}
        </DataTable>
      )}
      <Dialog
        open={!!edit}
        onClose={save.isPending ? undefined : closeEditor}
        fullWidth
        maxWidth="sm"
      >
        <ValidatedForm schema={scheduleSchema} value={edit} onValid={() => save.mutateAsync()} busy={save.isPending}>
        {save.error && <Alert severity="error">{save.error.message}</Alert>}
        <DialogTitle>
          {edit?.id ? "Редактирование расписания" : "Новое расписание"}
        </DialogTitle>
        <DialogContent>
          <Stack spacing={2} pt={1}>
            {edit?.retryAt && <Alert severity="info">Сохранение сбросит ожидание повтора.</Alert>}
            <TextField name="name"
              required
              label="Название"
              value={edit?.name ?? ""}
              onChange={(e) =>
                setEdit((v) => ({ ...v!, name: e.target.value }))
              }
            />
            <TextField name="intervalMonths"
              required
              type="number"
              label="Интервал, месяцев"
              inputProps={{ min: 1, max: 1200, step: 1 }}
              value={Number.isNaN(edit?.intervalMonths) ? "" : edit?.intervalMonths ?? 12}
              onChange={(e) =>
                setEdit((v) => ({
                  ...v!,
                  intervalMonths: e.target.value === "" ? NaN : Number(e.target.value),
                }))
              }
            />
            <TextField name="nextRunAt"
              required
              type="datetime-local"
              label="Следующий запуск"
              helperText="При выборе другой даты изменится число повторов. В коротком месяце проверка запустится в последний день."
              InputLabelProps={{ shrink: true }}
              value={edit?.nextRunAt ?? ""}
              onChange={(e) =>
                setEdit((v) => ({ ...v!, nextRunAt: e.target.value }))
              }
            />
            <FormControl name="launchMode">
              <InputLabel>Режим запуска</InputLabel>
              <Select
                label="Режим запуска"
                value={edit?.launchMode ?? "CreateDraft"}
                onChange={(e) =>
                  setEdit((v) => ({ ...v!, launchMode: e.target.value }))
                }
              >
                <MenuItem value="CreateDraft">Создать черновик</MenuItem>
                <MenuItem value="AutoStart">Автоматически запустить</MenuItem>
              </Select>
            </FormControl>
            {edit?.launchMode === "AutoStart" && (
              <Alert severity="warning">
                Система автоматически сформирует снимок реестра, назначит
                исполнителя и запустит проверку.
              </Alert>
            )}
            <FormControlLabel
              control={
                <Checkbox
                  checked={!!edit?.isEnabled}
                  onChange={(e) =>
                    setEdit((v) => ({ ...v!, isEnabled: e.target.checked }))
                  }
                />
              }
              label="Расписание включено"
            />
          </Stack>
        </DialogContent>
        <DialogActions>
          <Button onClick={closeEditor} disabled={save.isPending}>Отмена</Button>
          <Button
            variant="contained"
            disabled={save.isPending}
            type="submit"
          >
            Сохранить
          </Button>
        </DialogActions>
        </ValidatedForm>
      </Dialog>
      <ConfirmDialog
        open={!!del}
        title="Удалить расписание?"
        text="Запланированные автоматические запуски будут прекращены."
        busy={remove.isPending}
        onCancel={() => { remove.reset(); setDel(null); }}
        onConfirm={() => del && remove.mutate(del)}
      />
    </>
  );
}

const emptyEvent = () => ({ eventId: "", employeeId: "", occurredAt: toLocalDateTimeInput(), details: "" });

export function EventsPage() {
  const qc = useQueryClient();
  const q = usePagedList<IntegrationEvent>(queryKeys.events, 'inventory-events', {}, 30_000);
  const [retryEvent, setRetryEvent] = useState<IntegrationEvent | null>(null);
  const retry = useMutation({
    mutationFn: api.retryEvent,
    onSuccess: () => {
      setRetryEvent(null);
      void qc.invalidateQueries({ queryKey: queryKeys.events });
    },
  });
  const emps = useQuery({
    queryKey: queryKeys.employees,
    queryFn: () => api.employees(),
  });
  const [open, setOpen] = useState(false);
  const [v, setV] = useState(emptyEvent);
  const create = useMutation({
    mutationFn: () =>
      api.createMolEvent({
        ...v,
        occurredAt: dateTimeToIso(v.occurredAt),
        details: v.details || null,
      }),
    onSuccess: () => {
      setOpen(false);
      void qc.invalidateQueries({ queryKey: queryKeys.events });
    },
  });
  return (
    <>
      <PageHeader
        title="Кадровые события"
        description="Очередь событий, создающих событийные инвентаризации"
        action={
          <Stack direction="row">
            <Button startIcon={<Refresh />} onClick={() => void q.refetch()}>
              Обновить
            </Button>
            <Button
              variant="contained"
              startIcon={<Add />}
              onClick={() => { create.reset(); setV(emptyEvent()); setOpen(true); }}
            >
              Смена МОЛ
            </Button>
          </Stack>
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
            "Событие",
            "Статус",
            "Попытки",
            "Следующая попытка",
            "Последняя ошибка",
            "Результат обработки",
            "Инвентаризация",
            "Получено",
            "Действия",
          ]}
          empty={!q.data?.length}
        >
          {q.data?.map((x) => (
            <TableRow key={x.id}>
              <TableCell>
                <PrimaryWithId primary={label(x.type)} id={x.eventId} />
              </TableCell>
              <TableCell>
                <StatusChip value={x.status} />
              </TableCell>
              <TableCell>{x.attempts}</TableCell>
              <TableCell>{formatDateTime(x.nextAttemptAt)}</TableCell>
              <TableCell>{x.lastError ?? "—"}</TableCell>
              <TableCell>{x.resultMessage ?? "—"}</TableCell>
              <TableCell>
                {x.createdInventoryId ? (
                  <Button
                    component={Link}
                    to={`/inventories/${x.createdInventoryId}`}
                    startIcon={<LinkIcon />}
                  >
                    <PrimaryWithId
                      primary={x.createdInventoryNumber ?? "Открыть инвентаризацию"}
                      id={x.createdInventoryId}
                    />
                  </Button>
                ) : (
                  "—"
                )}
              </TableCell>
              <TableCell>{formatDateTime(x.receivedAt)}</TableCell>
              <TableCell>
                {x.status === 'Failed' && !x.createdInventoryId && <Button
                  startIcon={<Refresh />}
                  onClick={() => { retry.reset(); setRetryEvent(x); }}
                >Повторить обработку</Button>}
              </TableCell>
            </TableRow>
          ))}
        </DataTable>
      )}
      <ConfirmDialog
        open={!!retryEvent}
        title="Повторить обработку события?"
        text={`Событие «${retryEvent?.eventId ?? ''}» будет снова поставлено в очередь. Исправьте причину ошибки до повтора. Начнётся новый цикл из пяти попыток.`}
        busy={retry.isPending}
        error={retry.error}
        onCancel={() => setRetryEvent(null)}
        onConfirm={() => retryEvent && retry.mutate(retryEvent.id)}
      />
      <Dialog
        open={open}
        onClose={create.isPending ? undefined : () => setOpen(false)}
        fullWidth
        maxWidth="sm"
      >
        <ValidatedForm schema={eventSchema} value={v} onValid={() => create.mutateAsync()} busy={create.isPending}>
        {create.error && <Alert severity="error">{create.error.message}</Alert>}
        <DialogTitle>Событие смены МОЛ</DialogTitle>
        <DialogContent>
          <Stack spacing={2} pt={1}>
            <TextField name="eventId"
              required
              label="Внешний Event ID"
              value={v.eventId}
              onChange={(e) => setV({ ...v, eventId: e.target.value })}
            />
            <FormControl required name="employeeId">
              <InputLabel>Сотрудник</InputLabel>
              <Select
                label="Сотрудник"
                value={v.employeeId}
                onChange={(e) => setV({ ...v, employeeId: e.target.value })}
              >
                {emps.data
                  ?.filter((x) => x.isMateriallyResponsible)
                  .map((x) => (
                    <MenuItem key={x.id} value={x.id}>
                      {x.fullName}
                    </MenuItem>
                  ))}
              </Select>
            </FormControl>
            <TextField name="occurredAt"
              required
              type="datetime-local"
              label="Дата события"
              InputLabelProps={{ shrink: true }}
              value={v.occurredAt}
              onChange={(e) => setV({ ...v, occurredAt: e.target.value })}
            />
            <TextField name="details"
              multiline
              minRows={2}
              label="Комментарий"
              value={v.details}
              onChange={(e) => setV({ ...v, details: e.target.value })}
            />
          </Stack>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setOpen(false)} disabled={create.isPending}>Отмена</Button>
          <Button
            variant="contained"
            disabled={create.isPending}
            type="submit"
          >
            Зарегистрировать
          </Button>
        </DialogActions>
        </ValidatedForm>
      </Dialog>
    </>
  );
}

export { AuditPage } from './AuditPage';
