import { Pagination, Stack, TextField, Typography } from '@mui/material';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '../api/api';
export function usePagedList<T>(key: readonly unknown[], endpoint: string, params: Record<string, unknown> = {}, refetchInterval?: number) {
  const scope = JSON.stringify(params);
  const [state, setState] = useState({ page: 1, search: '', scope });
  const page = state.scope === scope ? state.page : 1;
  const query = useQuery({ queryKey: [...key, 'paged', params, page, state.search], queryFn: () => api.paged<T>(endpoint, { ...params, page, pageSize: 20, search: state.search }), placeholderData: (previous, previousQuery) => JSON.stringify(previousQuery?.queryKey.slice(0, key.length)) === JSON.stringify(key) ? previous : undefined, refetchInterval });
  const controls = <Stack direction={{ xs: 'column', md: 'row' }} spacing={2} alignItems="center" my={2}>
    <TextField size="small" label="Поиск в списке" value={state.search} onChange={event => setState({ page: 1, search: event.target.value, scope })} />
    <Typography color="text.secondary">{query.isFetching ? 'Обновление списка…' : `Всего: ${query.data?.totalCount ?? 0} · по 20 строк`}</Typography>
    <Pagination disabled={query.isFetching} count={Math.max(1, Math.ceil((query.data?.totalCount ?? 0) / 20))} page={query.data?.page ?? page} onChange={(_, value) => setState({ ...state, page: value, scope })} />
  </Stack>;
  return { ...query, data: query.data?.items, controls };
}
