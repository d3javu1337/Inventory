import { Add, OpenInNew, RestartAlt } from '@mui/icons-material';
import { Button, FormControl, IconButton, InputLabel, MenuItem, Pagination, Select, Stack, TableCell, TableRow, TextField, Typography } from '@mui/material';
import { useQuery } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { api } from '../../api/api';
import { DataTable, ErrorState, LoadingState, PageHeader, StatusChip } from '../../components/Ui';
import { formatDateTime } from '../../shared/formatters';
import { label } from '../../shared/labels';
import { queryKeys } from '../../shared/queryKeys';

const statuses = ['Draft', 'NeedsAttention', 'Active', 'AwaitingConfirmation', 'ReviewRequired', 'Completed', 'Cancelled'];
const types = ['Planned', 'EventDriven', 'Manual'];
const reasons = ['AnnualReporting', 'MolChange', 'MolDismissal', 'AssetTransfer', 'LossOrDamage', 'Emergency', 'Reorganization', 'Liquidation', 'Other'];

export function InventoriesPage() {
  const [params, setParams] = useSearchParams();
  const search = params.get('search') ?? '';
  const [debouncedSearch, setDebouncedSearch] = useState(search.trim());
  useEffect(() => {
    const timer = window.setTimeout(() => setDebouncedSearch(search.trim()), 300);
    return () => window.clearTimeout(timer);
  }, [search]);
  const requestedPage = Number(params.get('page') ?? 1);
  const filters = {
    status: params.get('status') ?? '', type: params.get('type') ?? '', reason: params.get('reason') ?? '',
    search: debouncedSearch, page: Number.isInteger(requestedPage) && requestedPage > 0 ? requestedPage : 1, pageSize: 50,
  };
  const update = (key: string, value: string) => {
    const next = new URLSearchParams(params);
    if (value) next.set(key, value); else next.delete(key);
    if (key !== 'page') next.delete('page');
    setParams(next, { replace: true, flushSync: true });
  };
  const q = useQuery({ queryKey: queryKeys.inventories(filters), queryFn: () => api.inventories(filters), refetchInterval: 30_000 });
  return <>
    <PageHeader title="Инвентаризации" description="Плановые, событийные и ручные проверки" action={<Button component={Link} to="/inventories/new" variant="contained" startIcon={<Add />}>Создать</Button>} />
    <Stack direction={{ xs: 'column', md: 'row' }} gap={1.5} mb={2} flexWrap="wrap">
      <TextField size="small" label="Поиск инвентаризаций" placeholder="Номер, UUID, МОЛ или исполнитель" value={search} onChange={e => update('search', e.target.value)} inputProps={{ maxLength: 200 }} sx={{ minWidth: 280 }} />
      {[{ key: 'status', title: 'Статус', values: statuses }, { key: 'type', title: 'Тип', values: types }, { key: 'reason', title: 'Основание', values: reasons }].map(filter => <FormControl key={filter.key} size="small" sx={{ minWidth: 180 }}>
        <InputLabel id={`inventory-${filter.key}`}>{filter.title}</InputLabel>
        <Select labelId={`inventory-${filter.key}`} label={filter.title} value={params.get(filter.key) ?? ''} onChange={e => update(filter.key, e.target.value)}>
          <MenuItem value="">Все</MenuItem>
          {filter.values.map(value => <MenuItem key={value} value={value}>{label(value)}</MenuItem>)}
        </Select>
      </FormControl>)}
      <Button startIcon={<RestartAlt />} onClick={() => { setDebouncedSearch(''); setParams({}); }}>Сбросить</Button>
    </Stack>
    {q.isLoading ? <LoadingState /> : q.error ? <ErrorState error={q.error} retry={() => void q.refetch()} /> : <>
      <Typography color="text.secondary" mb={1}>Всего: {q.data?.totalCount ?? 0}</Typography>
      <DataTable headers={['Номер', 'Тип', 'Основание', 'МОЛ / область', 'Исполнитель', 'Плановая дата', 'Статус', 'Всего', 'Найдено', 'Не найдено', 'Ожидает', '']} empty={!q.data?.items.length}>
        {q.data?.items.map(x => <TableRow key={x.id}>
          <TableCell>{x.number}</TableCell><TableCell>{label(x.type)}</TableCell><TableCell>{label(x.reason)}</TableCell>
          <TableCell>{x.targetEmployeeName ?? (x.type === 'EventDriven' ? 'Не указан' : 'Все активные ОС')}</TableCell>
          <TableCell>{x.assigneeName ?? 'Не назначен'}</TableCell>
          <TableCell>{formatDateTime(x.plannedAt)}</TableCell><TableCell><StatusChip value={x.status} /></TableCell>
          <TableCell>{x.total}</TableCell><TableCell>{x.found}</TableCell><TableCell>{x.notFound}</TableCell><TableCell>{x.pending}</TableCell>
          <TableCell><IconButton component={Link} to={`/inventories/${x.id}`} aria-label={`Открыть ${x.number}`}><OpenInNew /></IconButton></TableCell>
        </TableRow>)}
      </DataTable>
      <Stack alignItems="center" mt={2}><Pagination disabled={q.isFetching} count={Math.max(1, Math.ceil((q.data?.totalCount ?? 0) / filters.pageSize))} page={q.data?.page ?? filters.page} onChange={(_, page) => update('page', String(page))} /></Stack>
    </>}
  </>;
}
