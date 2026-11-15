import {
  Cancel,
  ContentCopy,
  Download,
  PlayArrow,
  Refresh,
  SwapHoriz,
} from "@mui/icons-material";
import {
  Alert,
  Box,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Grid,
  InputLabel,
  LinearProgress,
  MenuItem,
  Paper,
  Pagination,
  TextField,
  Select,
  Stack,
  Tab,
  TableCell,
  TableRow,
  Tabs,
  Typography,
} from "@mui/material";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { api } from "../../api/api";
import { useFileAction } from "../../components/useFileAction";
import {
  ConfirmDialog,
  DataTable,
  ErrorState,
  LoadingState,
  PageHeader,
  PrimaryWithId,
  StatusChip,
} from "../../components/Ui";
import { formatDateTime, formatMoney, percent } from "../../shared/formatters";
import { label } from "../../shared/labels";
import { queryKeys } from "../../shared/queryKeys";
import { ValidatedForm, ValidatedTextField, ValidatedControl as FormControl } from "../../components/ValidatedForm";
import { assigneeSchema, cloneInventorySchema, toLocalDateTimeInput, dateTimeToIso } from "../../shared/validation";
export function InventoryDetailPage() {
  const file = useFileAction();
  const { id = "" } = useParams();
  const qc = useQueryClient();
  const navigate = useNavigate();
  const [cloneOpen, setCloneOpen] = useState(false);
  const [cloneDate, setCloneDate] = useState(toLocalDateTimeInput());
  const [tab, setTab] = useState(0);
  const [page, setPage] = useState(1);
  const [search, setSearch] = useState('');
  const [confirm, setConfirm] = useState<"start" | "cancel" | null>(null);
  const [replace, setReplace] = useState({
    open: false,
    oldUserId: "",
    newUserId: "",
  });
  const q = useQuery({
    queryKey: [...queryKeys.inventory(id), tab, page, search],
    queryFn: () => api.inventory(id, { page, pageSize: 20, search, filter: tab === 2 ? 'issues' : tab === 3 ? 'unexpected' : 'all' }),
    placeholderData: (previous, previousQuery) => previousQuery?.queryKey[1] === id ? previous : undefined,
    refetchInterval: (query) =>
      query.state.data?.status === "Active" ? 15_000 : false,
  });
  const users = useQuery({ queryKey: queryKeys.users, queryFn: api.users });
  const action = useMutation({
    mutationFn: (kind: "start" | "cancel") =>
      kind === "start" ? api.startInventory(id) : api.cancelInventory(id),
    onSuccess: () => {
      setConfirm(null);
      void qc.invalidateQueries({ queryKey: queryKeys.inventory(id) });
      void qc.invalidateQueries({ queryKey: ["inventories"] });
    },
  });
  const clone = useMutation({
    mutationFn: () => api.cloneInventory(id, dateTimeToIso(cloneDate)),
    onSuccess: (created) => {
      setCloneOpen(false);
      void qc.invalidateQueries({ queryKey: ["inventories"] });
      navigate(`/inventories/${created.id}`);
    },
  });
  const swap = useMutation({
    mutationFn: () =>
      api.replaceAssignee(id, {
        oldUserId: replace.oldUserId,
        newUserId: replace.newUserId,
      }),
    onSuccess: () => {
      setReplace({ ...replace, open: false });
      void qc.invalidateQueries({ queryKey: queryKeys.inventory(id) });
    },
  });
  if (q.isLoading) return <LoadingState />;
  if (q.error)
    return <ErrorState error={q.error} retry={() => void q.refetch()} />;
  const x = q.data!;
  const assignee = x.assignee ?? x.commission[0];
  const found = x.counts?.found ?? x.items.filter((i) => i.result === "Found").length;
  const total = x.counts?.total ?? x.items.length;
  const unexpectedCount = x.counts?.unexpected ?? x.unexpectedScans.length;
  const rowCount = tab === 3 ? x.unexpectedTotalCount ?? x.unexpectedScans.length : x.itemsTotalCount ?? x.items.length;
  const discrepancies = x.items.filter(
    (i) =>
      i.result === "NotFound" ||
      i.condition === "Damaged" ||
      i.condition === "Unusable"
  );
  return (
    <>
      <PageHeader
        title={`Инвентаризация ${x.number}`}
        description={`${label(x.type)} · ${label(x.reason)}`}
        action={
          <Stack direction="row" gap={1} flexWrap="wrap">
            <Button component={Link} to={`/audit?entityType=Inventory&entityId=${id}`}>История действий</Button>
            <Button startIcon={<Refresh />} onClick={() => void q.refetch()}>
              Обновить
            </Button>
            {["Draft", "ReviewRequired", "NeedsAttention"].includes(
              x.status
            ) && (
              <Button
                variant="contained"
                startIcon={<PlayArrow />}
                onClick={() => { action.reset(); setConfirm("start"); }}
              >
                Запустить
              </Button>
            )}
            {!["Completed", "Cancelled"].includes(x.status) && (
              <Button
                color="error"
                startIcon={<Cancel />}
                onClick={() => { action.reset(); setConfirm("cancel"); }}
              >
                Отменить
              </Button>
            )}
            {x.status === "Completed" && (
              <>
                <Button
                  startIcon={<Download />}
                  onClick={() =>
                    void file.run(
                      `/api/v1/inventories/${id}/reports/inv-1.xlsx`
                    )
                  }
                >
                  XLSX
                </Button>
                <Button
                  startIcon={<Download />}
                  onClick={() =>
                    void file.run(`/api/v1/inventories/${id}/reports/inv-1.pdf`)
                  }
                >
                  PDF
                </Button>
              </>
            )}
            {["Completed", "Cancelled"].includes(x.status) && (
              <Button startIcon={<ContentCopy />} onClick={() => { clone.reset(); setCloneDate(toLocalDateTimeInput()); setCloneOpen(true); }}>
                Создать на основе этой
              </Button>
            )}
          </Stack>
        }
      />
      {file.error && <Alert severity="error" sx={{ mb: 2 }}>{file.error}</Alert>}
      {x.sourceInventoryId && (
        <Alert severity="info" sx={{ mb: 2 }}>
          Создана на основе <Link to={`/inventories/${x.sourceInventoryId}`}>{x.sourceInventoryNumber ?? "предыдущей проверки"}</Link>. Результаты исходной проверки не изменяются.
        </Alert>
      )}
      <Grid container spacing={2} mb={2}>
        <Grid item xs={12} md={8}>
          <Paper sx={{ p: 2.5 }}>
            <Stack direction="row" justifyContent="space-between" mb={1}>
              <Typography variant="h6">Прогресс</Typography>
              <StatusChip value={x.status} />
            </Stack>
            <Typography variant="h4" mb={1}>
              {percent(found, total)}%
            </Typography>
            <LinearProgress
              variant="determinate"
              value={percent(found, total)}
              sx={{ height: 10, borderRadius: 5 }}
            />
            <Stack direction="row" gap={3} mt={2} flexWrap="wrap">
              <Typography>
                Всего: <b>{total}</b>
              </Typography>
              <Typography color="success.main">
                Найдено: <b>{found}</b>
              </Typography>
              <Typography color="error.main">
                Не найдено: <b>{x.counts?.notFound ?? x.items.filter((i) => i.result === "NotFound").length}</b>
              </Typography>
              <Typography color="warning.main">
                Повреждено / непригодно: <b>{x.counts?.damaged ?? x.items.filter((i) => i.result === "Found" && (i.condition === "Damaged" || i.condition === "Unusable")).length}</b>
              </Typography>
              <Typography color="text.secondary">
                Посторонние QR: <b>{unexpectedCount}</b>
              </Typography>
            </Stack>
          </Paper>
        </Grid>
        <Grid item xs={12} md={4}>
          <Paper sx={{ p: 2.5, height: "100%" }}>
            <Typography color="text.secondary">Плановая дата</Typography>
            <Typography fontWeight={700}>
              {formatDateTime(x.plannedAt)}
            </Typography>
            <Typography color="text.secondary" mt={1}>
              Начало
            </Typography>
            <Typography>{formatDateTime(x.startedAt)}</Typography>
          </Paper>
        </Grid>
      </Grid>
      <Paper>
        <Tabs value={tab} onChange={(_, v) => { setTab(v); setPage(1); setSearch(''); }} variant="scrollable">
          <Tab label={`Состав (${total})`} />
          <Tab label="Исполнитель" />
          <Tab label={`Расхождения ОС (${x.counts?.discrepancies ?? discrepancies.length})`} />
          <Tab label={`Посторонние QR (${unexpectedCount})`} />
        </Tabs>
        <Box p={2}>
          {tab !== 1 && <TextField label={tab === 3 ? 'Поиск QR или ОС' : 'Поиск по номеру, названию, помещению'} value={search} onChange={e => { setSearch(e.target.value); setPage(1); }} fullWidth sx={{ mb: 2 }} />}
          {tab === 0 && (
            <DataTable
              headers={[
                "Инв. №",
                "Наименование",
                "Счет",
                "Стоимость",
                "Помещение",
                "МОЛ",
                "Результат",
                "Состояние",
              ]}
              empty={!x.items.length}
            >
              {x.items.map((i) => (
                <TableRow key={i.id}>
                  <TableCell>{i.inventoryNumber}</TableCell>
                  <TableCell>{i.name}</TableCell>
                  <TableCell>{i.accountingAccount}</TableCell>
                  <TableCell>{formatMoney(i.initialCostExact ?? i.initialCost)}</TableCell>
                  <TableCell>{i.locationName}</TableCell>
                  <TableCell>{i.materiallyResponsibleFullName}</TableCell>
                  <TableCell>
                    <StatusChip value={i.result} />
                  </TableCell>
                  <TableCell>{label(i.condition)}</TableCell>
                </TableRow>
              ))}
            </DataTable>
          )}
          {tab === 1 && (
            <>
              <Stack alignItems="flex-end" mb={1}>
                {["Draft", "NeedsAttention", "ReviewRequired"].includes(x.status) && (
                  <Button
                    startIcon={<SwapHoriz />}
                    onClick={() => setReplace({ open: true, oldUserId: assignee?.userId ?? "00000000-0000-0000-0000-000000000000", newUserId: "" })}
                  >
                    {assignee ? "Заменить исполнителя" : "Назначить исполнителя"}
                  </Button>
                )}
              </Stack>
              <DataTable
                headers={[
                  "Роль",
                  "Логин",
                  "Сотрудник",
                  "Статус",
                  "Подтверждено",
                  "Причина отклонения",
                ]}
              >
                {(assignee ? [assignee] : []).map((m) => (
                  <TableRow key={m.id}>
                    <TableCell>{label(m.role)}</TableCell>
                    <TableCell>{m.login}</TableCell>
                    <TableCell>{m.employeeName ?? "—"}</TableCell>
                    <TableCell>
                      <StatusChip value={m.confirmationStatus} />
                    </TableCell>
                    <TableCell>{formatDateTime(m.confirmedAt)}</TableCell>
                    <TableCell>{m.rejectionReason ?? "—"}</TableCell>
                  </TableRow>
                ))}
              </DataTable>
            </>
          )}
          {tab === 2 && (
            <DataTable
              headers={[
                "Инв. №",
                "Наименование",
                "Результат",
                "Состояние",
                "Комментарий",
              ]}
              empty={!discrepancies.length}
            >
              {discrepancies.map((i) => (
                <TableRow key={i.id}>
                  <TableCell>{i.inventoryNumber}</TableCell>
                  <TableCell>{i.name}</TableCell>
                  <TableCell>
                    <StatusChip value={i.result} />
                  </TableCell>
                  <TableCell>{label(i.condition)}</TableCell>
                  <TableCell>{i.comment ?? "—"}</TableCell>
                </TableRow>
              ))}
            </DataTable>
          )}
          {tab === 3 && (
            <>
              <Alert severity="info" sx={{ mb: 2 }}>
                Неизвестные и внеобластные QR фиксируются в акте, но не
                блокируют завершение проверки.
              </Alert>
              <DataTable
                headers={["Сканирование", "Распознанное ОС", "Время"]}
                empty={!x.unexpectedScans.length}
              >
                {x.unexpectedScans.map((s) => (
                  <TableRow key={s.id}>
                    <TableCell>
                      <PrimaryWithId
                        primary={<StatusChip value={s.type} />}
                        id={s.rawValue}
                      />
                    </TableCell>
                    <TableCell>
                      {s.recognizedAssetId ? (
                        <PrimaryWithId
                          primary={
                            [s.recognizedInventoryNumber, s.recognizedAssetName]
                              .filter(Boolean)
                              .join(" — ") || "Распознанное ОС"
                          }
                          id={s.recognizedAssetId}
                        />
                      ) : (
                        "—"
                      )}
                    </TableCell>
                    <TableCell>{formatDateTime(s.scannedAt)}</TableCell>
                  </TableRow>
                ))}
              </DataTable>
            </>
          )}
          {tab !== 1 && <Stack spacing={1} alignItems="center" mt={2}><Typography color="text.secondary">Всего по фильтру: {rowCount} · по 20 строк</Typography><Pagination count={Math.max(1, Math.ceil(rowCount / 20))} page={x.page ?? page} onChange={(_, value) => setPage(value)} /></Stack>}
        </Box>
      </Paper>
      {x.status !== "Completed" && (
        <Alert severity="info" sx={{ mt: 2 }}>
          Отчеты ИНВ-1 станут доступны после подтверждения исполнителем.
        </Alert>
      )}
      <ConfirmDialog
        open={!!confirm}
        title={
          confirm === "start"
            ? "Запустить инвентаризацию?"
            : "Отменить инвентаризацию?"
        }
        text={
          confirm === "start"
            ? "Будет зафиксирован снимок реестра и назначен один исполнитель."
            : "Проверка будет переведена в статус «Отменена»."
        }
        busy={action.isPending}
        error={action.error}
        onCancel={() => setConfirm(null)}
        onConfirm={() => confirm && action.mutate(confirm)}
      />
      <Dialog open={cloneOpen} onClose={() => { if (!clone.isPending) setCloneOpen(false); }} fullWidth maxWidth="sm">
        <ValidatedForm schema={cloneInventorySchema} value={{ plannedAt: cloneDate }} onValid={() => clone.mutateAsync()} busy={clone.isPending}>
          <DialogTitle>Новая проверка на основе {x.number}</DialogTitle>
          <DialogContent>
            {clone.error && <Alert severity="error" sx={{ mb: 2 }}>{clone.error.message}</Alert>}
            <Alert severity="info" sx={{ mb: 2 }}>Сохранятся тип, основание и область. Исполнитель выбирается заново; его можно заменить до запуска. Новый снимок будет создан при запуске. Сканы, подтверждения и отчеты не копируются.</Alert>
            <ValidatedTextField name="plannedAt" label="Плановая дата новой проверки" type="datetime-local" required fullWidth InputLabelProps={{ shrink: true }} value={cloneDate} onChange={e => setCloneDate(e.target.value)} />
          </DialogContent>
          <DialogActions>
            <Button disabled={clone.isPending} onClick={() => setCloneOpen(false)}>Отмена</Button>
            <Button type="submit" variant="contained" disabled={clone.isPending}>Создать черновик</Button>
          </DialogActions>
        </ValidatedForm>
      </Dialog>
      <Dialog
        open={replace.open}
        onClose={() => setReplace({ ...replace, open: false })}
        fullWidth
        maxWidth="sm"
      >
        <ValidatedForm schema={assigneeSchema} value={replace} onValid={() => swap.mutateAsync()} busy={swap.isPending}>
        <DialogTitle>Замена исполнителя</DialogTitle>
        <DialogContent>
          {swap.error && <Alert severity="error">{swap.error.message}</Alert>}
          <Stack spacing={2} pt={1}>
            <FormControl name="oldUserId">
              <InputLabel>Текущий исполнитель</InputLabel>
              <Select
                label="Текущий исполнитель"
                value={replace.oldUserId}
                onChange={(e) =>
                  setReplace({ ...replace, oldUserId: e.target.value })
                }
              >
                {!assignee && <MenuItem value="00000000-0000-0000-0000-000000000000">Не назначен</MenuItem>}
                {(assignee ? [assignee] : []).map((m) => (
                  <MenuItem key={m.userId} value={m.userId}>
                    {m.employeeName ?? m.login} — {label(m.role)}
                  </MenuItem>
                ))}
              </Select>
            </FormControl>
            <FormControl name="newUserId">
              <InputLabel>Новый исполнитель</InputLabel>
              <Select
                label="Новый исполнитель"
                value={replace.newUserId}
                onChange={(e) =>
                  setReplace({ ...replace, newUserId: e.target.value })
                }
              >
                {users.data
                  ?.filter(
                    (u) =>
                      u.role === "InventoryEmployee" &&
                      !u.isBlocked &&
                      assignee?.userId !== u.id
                  )
                  .map((u) => (
                    <MenuItem key={u.id} value={u.id}>
                      {u.employeeName ?? u.login}
                    </MenuItem>
                  ))}
              </Select>
            </FormControl>
          </Stack>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setReplace({ ...replace, open: false })}>
            Отмена
          </Button>
          <Button
            variant="contained"
            disabled={swap.isPending}
            type="submit"
          >
            Заменить
          </Button>
        </DialogActions>
        </ValidatedForm>
      </Dialog>
    </>
  );
}
