import { Alert, Button, MenuItem, Paper, Stack } from '@mui/material';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect, useRef, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { api } from '../../api/api';
import { ApiError } from '../../api/httpClient';
import type { AssetInput } from '../../api/types';
import { ErrorState, LoadingState, PageHeader } from '../../components/Ui';
import { ValidatedForm, ValidatedTextField as TextField } from '../../components/ValidatedForm';
import { assetSchema, toLocalDateTimeInput } from '../../shared/validation';
import { queryKeys } from '../../shared/queryKeys';
import { AssetQrPreview } from './AssetQrPreview';

const newAsset = (): AssetInput => ({ inventoryNumber: '', name: '', accountingAccount: '', serialNumber: '', manufactureYear: null,
  acceptedAt: toLocalDateTimeInput().slice(0, 10), initialCost: '', departmentId: '', locationId: '', materiallyResponsibleEmployeeId: '' });

// From 2^46 onwards JavaScript numbers cannot distinguish all adjacent cent values.
const legacyCostLimit = 2 ** 46;

export function AssetFormPage() {
  const { id } = useParams();
  const nav = useNavigate();
  const qc = useQueryClient();
  const [value, setValue] = useState<AssetInput>(newAsset);
  const [etag, setEtag] = useState('');
  const loadedId = useRef<string | null>(null);
  const item = useQuery({ queryKey: queryKeys.asset(id ?? ''), queryFn: () => api.asset(id!), enabled: !!id });
  const deps = useQuery({ queryKey: queryKeys.departments, queryFn: () => api.departments() });
  const locs = useQuery({ queryKey: queryKeys.locations(value.departmentId), queryFn: () => api.locations(value.departmentId), enabled: !!value.departmentId });
  const emps = useQuery({ queryKey: queryKeys.employees, queryFn: () => api.employees() });
  const applyItem = useCallback((result: Awaited<ReturnType<typeof api.asset>>) => {
    const { data, response } = result;
    setValue({ inventoryNumber: data.inventoryNumber, name: data.name, accountingAccount: data.accountingAccount,
      serialNumber: data.serialNumber, manufactureYear: data.manufactureYear, acceptedAt: data.acceptedAt,
      initialCost: data.initialCostExact ?? (data.initialCost < legacyCostLimit ? String(data.initialCost) : ''),
      departmentId: data.departmentId, locationId: data.locationId,
      materiallyResponsibleEmployeeId: data.materiallyResponsibleEmployeeId });
    setEtag(response.headers.get('ETag') ?? `"${data.version}"`);
    loadedId.current = id ?? null;
  }, [id]);
  useEffect(() => {
    if (!id && loadedId.current !== null) { setValue(newAsset()); setEtag(''); loadedId.current = null; }
    else if (id && item.data && loadedId.current !== id) applyItem(item.data);
  }, [id, item.data, applyItem]);
  const save = useMutation({
    mutationFn: () => {
      const body = assetSchema.parse(value);
      return id ? api.updateAsset(id, body, etag) : api.createAsset(body);
    },
    onSuccess: asset => {
      void qc.invalidateQueries({ queryKey: ['assets'] });
      void qc.invalidateQueries({ queryKey: queryKeys.asset(asset.id) });
      void qc.invalidateQueries({ queryKey: queryKeys.assetHistory(asset.id) });
      void qc.invalidateQueries({ queryKey: queryKeys.dashboard });
      nav(`/assets/${asset.id}`);
    },
  });
  const change = <K extends keyof AssetInput>(key: K, next: AssetInput[K]) => setValue(v => ({ ...v, [key]: next }));
  const catalogsError = deps.error ?? locs.error ?? emps.error;
  const archived = !!item.data?.data.archivedAt || item.data?.data.status === 'Archived';
  const needsExactCost = item.data && item.data.data.initialCostExact === undefined && item.data.data.initialCost >= legacyCostLimit;
  const staleVersion = save.error instanceof ApiError && save.error.status === 412;
  return <>
    <PageHeader title={id ? 'Редактирование ОС' : 'Новое основное средство'} />
    {item.isLoading ? <LoadingState /> : item.error ? <ErrorState error={item.error} /> : (
    <Stack direction={{ xs: 'column', lg: 'row' }} gap={2} alignItems="flex-start">
    <Paper sx={{ p: 3, width: '100%', maxWidth: 900, flex: 1, minWidth: 0 }}>
      <ValidatedForm schema={assetSchema} value={value} onValid={() => save.mutateAsync()} busy={save.isPending || archived} spacing={2}>
        {archived && <Alert severity="info">Архивное ОС доступно только для просмотра.</Alert>}
        {save.error && <Alert severity="error">{staleVersion
          ? 'Карточка была изменена другим пользователем. Обновите данные и повторите сохранение.' : save.error.message}</Alert>}
        {staleVersion && <Button disabled={item.isFetching} onClick={async () => {
          const result = await item.refetch();
          if (result.data && !result.error) { applyItem(result.data); save.reset(); }
        }}>Загрузить актуальную карточку и сбросить правки</Button>}
        {catalogsError && <ErrorState error={catalogsError} />}
        <TextField required name="inventoryNumber" label="Инвентарный номер" value={value.inventoryNumber} onChange={e => change('inventoryNumber', e.target.value)} inputProps={{ maxLength: 100 }} />
        <TextField required name="name" label="Наименование" value={value.name} onChange={e => change('name', e.target.value)} inputProps={{ maxLength: 500 }} />
        <TextField required name="accountingAccount" label="Бухгалтерский счет" value={value.accountingAccount} onChange={e => change('accountingAccount', e.target.value)} inputProps={{ maxLength: 50 }} />
        <TextField name="serialNumber" label="Серийный номер" value={value.serialNumber ?? ''} onChange={e => change('serialNumber', e.target.value || null)} inputProps={{ maxLength: 200 }} />
        <TextField name="manufactureYear" type="number" label="Год выпуска" value={value.manufactureYear ?? ''} inputProps={{ min: 1900, max: new Date().getFullYear() + 1, step: 1 }} onChange={e => change('manufactureYear', e.target.value === '' ? null : Number(e.target.value))} />
        <TextField required name="acceptedAt" type="date" label="Дата принятия" InputLabelProps={{ shrink: true }} value={value.acceptedAt} inputProps={{ min: '0001-01-01', max: '9999-12-31' }} onChange={e => change('acceptedAt', e.target.value)} />
        <TextField required name="initialCost" label="Первоначальная стоимость" value={value.initialCost}
          inputProps={{ inputMode: 'decimal', maxLength: 32 }} onChange={e => change('initialCost', e.target.value)}
          helperText={needsExactCost && !value.initialCost ? 'Введите точную стоимость по учётным данным.' : undefined} />
        <TextField select required name="departmentId" label="Подразделение" value={value.departmentId} onChange={e => setValue(v => ({ ...v, departmentId: e.target.value, locationId: '' }))}>
          {deps.data?.filter(x => !x.isArchived).map(x => <MenuItem key={x.id} value={x.id}>{x.code} — {x.name}</MenuItem>)}
        </TextField>
        <TextField select required name="locationId" label="Помещение" disabled={!value.departmentId || locs.isLoading} value={value.locationId} onChange={e => change('locationId', e.target.value)}>
          {locs.data?.filter(x => !x.isArchived).map(x => <MenuItem key={x.id} value={x.id}>{x.code} — {x.name}</MenuItem>)}
        </TextField>
        <TextField select required name="materiallyResponsibleEmployeeId" label="Материально ответственное лицо" value={value.materiallyResponsibleEmployeeId} onChange={e => change('materiallyResponsibleEmployeeId', e.target.value)}>
          {emps.data?.filter(x => x.isMateriallyResponsible && x.isEmployed && !x.isArchived).map(x => <MenuItem key={x.id} value={x.id}>{x.fullName}</MenuItem>)}
        </TextField>
        <Stack direction="row" justifyContent="flex-end" gap={1}>
          <Button onClick={() => nav(-1)}>Отмена</Button>
          <Button type="submit" variant="contained" disabled={archived || save.isPending || !!catalogsError || deps.isLoading || emps.isLoading || locs.isFetching}>Сохранить</Button>
        </Stack>
      </ValidatedForm>
    </Paper>
    {id && item.data && <AssetQrPreview asset={item.data.data} />}
    </Stack>)}
  </>;
}
