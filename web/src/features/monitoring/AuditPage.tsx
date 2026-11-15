import { Accordion, AccordionDetails, AccordionSummary, Alert, Button, FormControl, InputLabel, MenuItem, Pagination, Select, Stack, TableCell, TableRow, TextField, Typography } from '@mui/material';
import { useQuery } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { api } from '../../api/api';
import { DataTable, ErrorState, LoadingState, PageHeader, PrimaryWithId } from '../../components/Ui';
import { formatDateTime } from '../../shared/formatters';
import { label, labels } from '../../shared/labels';
import { queryKeys } from '../../shared/queryKeys';
import { dateSchema } from '../../shared/validation';

const systemId = '00000000-0000-0000-0000-000000000000';
const entityTypes = ['Organization', 'Department', 'Location', 'Employee', 'UserAccount', 'Asset', 'Inventory', 'InventorySchedule', 'IntegrationEvent'];
const actions = Object.keys(labels).filter(key => key.includes('.')).sort((a, b) => label(a).localeCompare(label(b), 'ru'));

export function AuditPage() {
  const [params, setParams] = useSearchParams();
  const search = params.get('search') ?? '';
  const [debouncedSearch, setDebouncedSearch] = useState(search.trim());
  useEffect(() => { const timer = window.setTimeout(() => setDebouncedSearch(search.trim()), 300); return () => window.clearTimeout(timer); }, [search]);
  const pageSize = 50;
  const requestedPage = Number(params.get('page') ?? 1);
  const page = Number.isInteger(requestedPage) && requestedPage > 0 ? requestedPage : 1;
  const filters = { search: debouncedSearch, action: params.get('action') ?? '', entityType: params.get('entityType') ?? '', entityId: params.get('entityId') ?? '', userId: params.get('userId') ?? '', dateFrom: params.get('dateFrom') ?? '', dateTo: params.get('dateTo') ?? '' };
  const dateError = [filters.dateFrom, filters.dateTo].some(value => value && !dateSchema.safeParse(value).success)
    ? 'Введите корректные даты периода.' : filters.dateFrom && filters.dateTo && filters.dateFrom > filters.dateTo ? 'Начальная дата не может быть позже конечной.' : '';
  const q = useQuery({ queryKey: queryKeys.audit(page, pageSize, filters), queryFn: () => api.audit(page, pageSize, filters), enabled: !dateError });
  const users = useQuery({ queryKey: queryKeys.users, queryFn: api.users });
  const update = (key: string, value: string) => {
    const next = new URLSearchParams(params);
    if (value) next.set(key, value); else next.delete(key);
    if (key !== 'page') next.delete('page');
    setParams(next, { replace: true, flushSync: true });
  };
  return <>
    <PageHeader title="Аудит" description="История действий. Даты периода — по Екатеринбургу, включая выбранный последний день." action={<Button disabled={!!dateError || q.isFetching} onClick={() => void q.refetch()}>Обновить</Button>} />
    <Stack direction={{ xs: 'column', md: 'row' }} spacing={1.5} flexWrap="wrap" useFlexGap mb={2}>
      <TextField size="small" label="Поиск в аудите" placeholder="Объект, пользователь или ID запроса" value={search} onChange={e => update('search', e.target.value)} inputProps={{ maxLength: 200 }} sx={{ minWidth: 280 }} />
      {[{ key: 'action', title: 'Действие', values: actions }, { key: 'entityType', title: 'Тип сущности', values: entityTypes }].map(filter => <FormControl key={filter.key} size="small" sx={{ minWidth: 200 }}>
        <InputLabel id={`audit-${filter.key}`}>{filter.title}</InputLabel>
        <Select labelId={`audit-${filter.key}`} label={filter.title} value={params.get(filter.key) ?? ''} onChange={e => update(filter.key, e.target.value)}>
          <MenuItem value="">Все</MenuItem>
          {!filter.values.includes(params.get(filter.key) ?? '') && params.get(filter.key) && <MenuItem value={params.get(filter.key)!}>{label(params.get(filter.key))}</MenuItem>}
          {filter.values.map(value => <MenuItem key={value} value={value}>{label(value)}</MenuItem>)}
        </Select>
      </FormControl>)}
      <FormControl size="small" sx={{ minWidth: 220 }}>
        <InputLabel id="audit-user">Пользователь</InputLabel>
        <Select labelId="audit-user" label="Пользователь" value={filters.userId} onChange={e => update('userId', e.target.value)}>
          <MenuItem value="">Все</MenuItem><MenuItem value={systemId}>Система</MenuItem>
          {filters.userId && filters.userId !== systemId && !users.data?.some(user => user.id === filters.userId) && <MenuItem value={filters.userId}>{filters.userId}</MenuItem>}
          {users.data?.map(user => <MenuItem key={user.id} value={user.id}>{user.employeeName ? `${user.employeeName} (${user.login})` : user.login}</MenuItem>)}
        </Select>
      </FormControl>
      <TextField size="small" label="ID сущности" value={filters.entityId} onChange={e => update('entityId', e.target.value)} inputProps={{ maxLength: 200 }} sx={{ minWidth: 280 }} />
      <TextField size="small" type="date" label="С даты" InputLabelProps={{ shrink: true }} value={filters.dateFrom} onChange={e => update('dateFrom', e.target.value)} />
      <TextField size="small" type="date" label="По дату" InputLabelProps={{ shrink: true }} value={filters.dateTo} onChange={e => update('dateTo', e.target.value)} />
      <Button onClick={() => { setDebouncedSearch(''); setParams({}); }}>Сбросить</Button>
    </Stack>
    {users.error && <Alert severity="warning" sx={{ mb: 2 }}>Не удалось загрузить список пользователей. Поиск и остальные фильтры доступны.</Alert>}
    {dateError ? <Alert severity="error">{dateError}</Alert> : q.isLoading ? <LoadingState /> : q.error ? <ErrorState error={q.error} retry={() => void q.refetch()} /> : <>
      <Typography color="text.secondary" mb={1}>Найдено действий: {q.data?.totalCount ?? 0}</Typography>
      <DataTable headers={['Дата', 'Действие', 'Сущность', 'Пользователь', 'Запрос', 'Данные']} empty={!q.data?.items.length}>
        {q.data?.items.map(x => <TableRow key={x.id}>
          <TableCell>{formatDateTime(x.createdAt)}</TableCell><TableCell>{label(x.action)}</TableCell>
          <TableCell sx={{ maxWidth: 260 }}><Typography variant="caption" color="text.secondary">{label(x.entityType)}</Typography><PrimaryWithId primary={x.entityDisplayName ?? label(x.entityType)} id={x.entityId} /></TableCell>
          <TableCell sx={{ maxWidth: 260 }}><PrimaryWithId primary={x.userDisplayName ?? (x.userId && x.userId !== systemId ? 'Пользователь недоступен' : 'Система')} id={x.userId} /></TableCell>
          <TableCell><PrimaryWithId primary={x.ipAddress ?? '—'} id={x.correlationId} /></TableCell>
          <TableCell sx={{ minWidth: 180 }}>{x.dataJson ? <Accordion disableGutters elevation={0}><AccordionSummary>Показать JSON</AccordionSummary><AccordionDetails><Typography component="pre" sx={{ fontFamily: 'monospace', fontSize: 12, whiteSpace: 'pre-wrap', wordBreak: 'break-word', m: 0 }}>{safeJson(x.dataJson)}</Typography></AccordionDetails></Accordion> : '—'}</TableCell>
        </TableRow>)}
      </DataTable>
      <Stack alignItems="center" mt={2}><Pagination disabled={q.isFetching} count={Math.max(1, Math.ceil((q.data?.totalCount ?? 0) / pageSize))} page={q.data?.page ?? page} onChange={(_, value) => update('page', String(value))} /></Stack>
    </>}
  </>;
}

function safeJson(value: string) {
  try { return JSON.stringify(JSON.parse(value), null, 2).replace(/("(?:password|token|secret)"\s*:\s*)"[^"]*"/gi, '$1"***"'); }
  catch { return value; }
}
