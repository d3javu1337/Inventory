import React from 'react';
import { fireEvent, render, waitFor } from '@testing-library/react-native';
import { InventoryListScreen } from '../src/screens/InventoryListScreen';
import { mobileApi } from '../src/api/mobileApi';
import { session } from '../src/auth/session';
import type { InventorySummary } from '../src/types/api';
let mockFocused = true;
jest.mock('@react-navigation/native', () => ({ useIsFocused: () => mockFocused }));
const mockUseInfiniteQuery = jest.fn((_options: unknown) => ({
  data: { pages: [{ items: [] as InventorySummary[], totalCount: 0 }] },
  isLoading: false,
  isError: false,
}));
jest.mock('@tanstack/react-query', () => ({
  useInfiniteQuery: (options: unknown) => mockUseInfiniteQuery(options),
}));
jest.mock('../src/api/mobileApi', () => ({
  mobileApi: { inventories: jest.fn(), logout: jest.fn() },
}));
jest.mock('../src/auth/session', () => ({
  session: {
    signOut: jest.fn().mockResolvedValue('own-refresh'),
  },
}));
beforeEach(() => { jest.clearAllMocks(); mockFocused = true; });
function screen() {
  return render(
    <InventoryListScreen
      {...({ navigation: {} } as unknown as React.ComponentProps<
        typeof InventoryListScreen
      >)}
    />,
  );
}
it('filters on the server with independent page caches', () => {
  const view = screen();
  expect(mockUseInfiniteQuery.mock.calls.at(-1)?.[0]).toMatchObject({
    queryKey: ['mobile-inventories', 'Active', ''],
    initialPageParam: 1,
  });
  fireEvent.press(view.getByText('Подтверждение'));
  const options = mockUseInfiniteQuery.mock.calls.at(-1)?.[0] as unknown as {
    queryKey: string[];
    queryFn: (args: { pageParam: number }) => void;
  };
  expect(options.queryKey).toEqual([
    'mobile-inventories',
    'AwaitingConfirmation',
    '',
  ]);
  options.queryFn({ pageParam: 2 });
  expect(mobileApi.inventories).toHaveBeenCalledWith(2, 'AwaitingConfirmation', '');
  fireEvent.press(view.getByText('Завершённые'));
  expect(mockUseInfiniteQuery.mock.calls.at(-1)?.[0]).toMatchObject({
    queryKey: ['mobile-inventories', 'Completed', ''],
  });
  fireEvent.press(view.getByText('Все'));
  expect(mockUseInfiniteQuery.mock.calls.at(-1)?.[0]).toMatchObject({
    queryKey: ['mobile-inventories', 'all', ''],
  });
});
it('refreshes only the focused active list with at most one page loaded', () => {
  const view = screen();
  const interval = (pages: unknown[]) => {
    const options = mockUseInfiniteQuery.mock.calls.at(-1)?.[0] as {
      refetchInterval: (query: { state: { data: { pages: unknown[] } } }) => number | false;
    };
    return options.refetchInterval({ state: { data: { pages } } });
  };
  expect(interval([{}])).toBe(15_000);
  expect(interval([{}, {}])).toBe(false);
  fireEvent.press(view.getByText('Все'));
  expect(interval([{}])).toBe(false);
  mockFocused = false;
  fireEvent.press(view.getByText('Активные'));
  expect(interval([{}])).toBe(false);
});
it('logs out locally even if the server is unavailable', async () => {
  jest.mocked(mobileApi.logout).mockRejectedValue(new Error('offline'));
  const view = screen();
  fireEvent.press(view.getByText('Выйти'));
  await waitFor(() => expect(session.signOut).toHaveBeenCalled());
});
it('clears the screen before a pending server logout and ignores a double tap', async () => {
  let complete!: () => void;
  jest.mocked(mobileApi.logout).mockImplementation(() => new Promise(resolve => { complete = resolve; }));
  const view = screen();
  fireEvent.press(view.getByText('Выйти')); fireEvent.press(view.getByText('Выйти'));
  await waitFor(() => expect(mobileApi.logout).toHaveBeenCalledTimes(1));
  expect(session.signOut).toHaveBeenCalledTimes(1);
  expect(jest.mocked(session.signOut).mock.invocationCallOrder[0]).toBeLessThan(jest.mocked(mobileApi.logout).mock.invocationCallOrder[0]);
  complete();
});
it('searches on the server, preserves search across categories and clears it', async () => {
  const view = screen();
  fireEvent.changeText(view.getByLabelText('Поиск инвентаризаций'), '  Петрова  ');
  await waitFor(() => expect(mockUseInfiniteQuery.mock.calls.at(-1)?.[0]).toMatchObject({ queryKey: ['mobile-inventories', 'Active', 'Петрова'] }));
  const options = mockUseInfiniteQuery.mock.calls.at(-1)?.[0] as { queryFn: (args: { pageParam: number }) => void };
  options.queryFn({ pageParam: 2 });
  expect(mobileApi.inventories).toHaveBeenCalledWith(2, 'Active', 'Петрова');
  fireEvent.press(view.getByText('Черновики'));
  expect(mockUseInfiniteQuery.mock.calls.at(-1)?.[0]).toMatchObject({ queryKey: ['mobile-inventories', 'Draft', 'Петрова'], initialPageParam: 1 });
  fireEvent.press(view.getByText('Доработка'));
  expect(mockUseInfiniteQuery.mock.calls.at(-1)?.[0]).toMatchObject({ queryKey: ['mobile-inventories', 'ReviewRequired', 'Петрова'] });
  fireEvent.press(view.getByText('Очистить'));
  expect(mockUseInfiniteQuery.mock.calls.at(-1)?.[0]).toMatchObject({ queryKey: ['mobile-inventories', 'ReviewRequired', ''] });
});
it('keeps the search field available during loading', () => {
  mockUseInfiniteQuery.mockReturnValueOnce({ data: { pages: [{ items: [], totalCount: 0 }] }, isLoading: true, isError: false });
  const view = screen();
  expect(view.getByLabelText('Поиск инвентаризаций')).toBeTruthy();
  expect(view.getByText('Загрузка проверок…')).toBeTruthy();
  expect(view.getByText('Все')).toBeTruthy();
});
it('shows the assigned employee and target MOL on the card', () => {
  mockUseInfiniteQuery.mockReturnValueOnce({ data: { pages: [{ items: [{ id: 'i', number: 'INV-1', type: 'EventDriven', reason: 'MolChange', status: 'Active', plannedAt: '', total: 1, found: 0, notFound: 0, pending: 1, assigneeName: 'Иванов Иван', targetEmployeeName: 'Петрова Анна' }], totalCount: 1 }] }, isLoading: false, isError: false });
  const view = screen();
  expect(view.getByText('МОЛ: Петрова Анна')).toBeTruthy();
  expect(view.getByText('Исполнитель: Иванов Иван')).toBeTruthy();
});
