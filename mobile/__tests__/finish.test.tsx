import React from 'react';
import { act, fireEvent, render, waitFor } from '@testing-library/react-native';
import { InventoryDetailsScreen } from '../src/screens/InventoryDetailsScreen';
import { mobileApi } from '../src/api/mobileApi';
import { Action } from '../src/ui/kit';

let mockRound = 1;
jest.mock('@tanstack/react-query', () => ({
  useQuery: () => ({
    data: {
      number: 'INV-TEST',
      round: mockRound,
      status: 'Active',
      reason: 'AnnualReporting',
      items: [],
      unexpectedScans: [],
      counts: {
        total: 2,
        found: 0,
        pending: 2,
        notFound: 0,
        damaged: 0,
        unexpected: 0,
      },
      commission: [
        {
          id: 'member',
          userId: 'worker',
          login: 'worker',
          confirmationStatus: 'Pending',
        },
      ],
    },
  }),
}));
jest.mock('../src/auth/session', () => ({
  session: { getUser: () => ({ id: 'worker' }) },
}));
jest.mock('../src/query/QueryProvider', () => ({
  queryClient: { invalidateQueries: jest.fn().mockResolvedValue(undefined) },
}));
jest.mock('../src/api/mobileApi', () => ({
  mobileApi: { finishScanning: jest.fn() },
}));
beforeEach(() => { jest.clearAllMocks(); mockRound = 1; });
function screen() {
  const props = {
    route: { params: { id: 'inventory' } },
    navigation: {},
  } as unknown as React.ComponentProps<typeof InventoryDetailsScreen>;
  const view = render(<InventoryDetailsScreen {...props} />);
  return { ...view, refresh: () => view.rerender(<InventoryDetailsScreen {...props} />) };
}
it('requires explicit confirmation and blocks two finish calls before the next render', async () => {
  let resolve!: () => void;
  jest.mocked(mobileApi.finishScanning).mockReturnValue(
    new Promise(done => {
      resolve = done;
    }),
  );
  const view = screen();
  fireEvent.press(view.getByText('Завершить обход'));
  expect(mobileApi.finishScanning).not.toHaveBeenCalled();
  mockRound = 2;
  view.refresh();
  const finish = view
    .UNSAFE_getAllByType(Action)
    .find(button => button.props.title === 'Завершить и проверить итоги')!.props
    .onPress;
  act(() => {
    finish();
    finish();
  });
  expect(mobileApi.finishScanning).toHaveBeenCalledTimes(1);
  expect(mobileApi.finishScanning).toHaveBeenCalledWith('inventory', 1);
  await act(async () => resolve());
  await waitFor(() => expect(view.getByText('Сканировать QR')).toBeTruthy());
});
it('keeps the confirmation and allows retry after a failed finish', async () => {
  jest
    .mocked(mobileApi.finishScanning)
    .mockRejectedValueOnce(new Error('Нет сети'))
    .mockResolvedValueOnce(undefined);
  const view = screen();
  fireEvent.press(view.getByText('Завершить обход'));
  fireEvent.press(view.getByText('Завершить и проверить итоги'));
  await waitFor(() => expect(view.getByText('Нет сети')).toBeTruthy());
  fireEvent.press(view.getByText('Завершить и проверить итоги'));
  await waitFor(() => expect(view.getByText('Сканировать QR')).toBeTruthy());
  expect(mobileApi.finishScanning).toHaveBeenCalledTimes(2);
});
