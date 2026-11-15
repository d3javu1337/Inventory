import React from 'react';
import { act, fireEvent, render, waitFor } from '@testing-library/react-native';
import { ConfirmationScreen } from '../src/screens/ConfirmationScreen';
import { mobileApi } from '../src/api/mobileApi';
import { queryClient } from '../src/query/QueryProvider';

jest.mock('../src/api/mobileApi', () => ({
  mobileApi: { confirm: jest.fn(), reject: jest.fn() },
}));
jest.mock('../src/query/QueryProvider', () => ({
  queryClient: { invalidateQueries: jest.fn().mockResolvedValue(undefined) },
}));
beforeEach(() => jest.clearAllMocks());
function screen() {
  const popTo = jest.fn();
  const props = {
    route: { params: { id: 'inventory', round: 3 } },
    navigation: { popTo },
  } as unknown as React.ComponentProps<typeof ConfirmationScreen>;
  return { ...render(<ConfirmationScreen {...props} />), popTo };
}
it('does not send an empty password via keyboard submit', () => {
  const view = screen();
  fireEvent(view.getByPlaceholderText('Пароль'), 'submitEditing');
  expect(
    view.getByText('Введите пароль для подтверждения действия.'),
  ).toBeTruthy();
  expect(mobileApi.confirm).not.toHaveBeenCalled();
});
it('requires a rejection reason inline without sending a request', () => {
  const view = screen();
  fireEvent.changeText(view.getByPlaceholderText('Пароль'), 'test-password');
  fireEvent.changeText(view.getByPlaceholderText('Причина отклонения'), '   ');
  fireEvent.press(view.getByText('Отклонить результаты'));
  expect(view.getByText('Укажите причину отклонения.')).toBeTruthy();
  expect(mobileApi.reject).not.toHaveBeenCalled();
});
it('blocks duplicate keyboard submits while a decision is pending', async () => {
  let resolve!: () => void;
  jest.mocked(mobileApi.confirm).mockReturnValue(
    new Promise(done => {
      resolve = done;
    }),
  );
  const view = screen();
  fireEvent.changeText(view.getByPlaceholderText('Пароль'), 'test-password');
  act(() => {
    fireEvent(view.getByPlaceholderText('Пароль'), 'submitEditing');
    fireEvent(view.getByPlaceholderText('Пароль'), 'submitEditing');
  });
  expect(mobileApi.confirm).toHaveBeenCalledTimes(1);
  expect(mobileApi.confirm).toHaveBeenCalledWith('inventory', 'test-password', 3);
  expect(view.popTo).not.toHaveBeenCalled();
  await act(async () => resolve());
  await waitFor(() =>
    expect(view.popTo).toHaveBeenCalledWith('Inventory', { id: 'inventory' }),
  );
  expect(queryClient.invalidateQueries).toHaveBeenCalledWith({
    queryKey: ['mobile-inventory', 'inventory'],
  });
  expect(view.getByPlaceholderText('Пароль').props.value).toBe('');
});
it('preserves inputs and allows retry after a network failure', async () => {
  jest
    .mocked(mobileApi.reject)
    .mockRejectedValueOnce(new Error('Нет сети'))
    .mockResolvedValueOnce(undefined);
  const view = screen();
  fireEvent.changeText(view.getByPlaceholderText('Пароль'), 'test-password');
  fireEvent.changeText(
    view.getByPlaceholderText('Причина отклонения'),
    '  Пересчитать имущество  ',
  );
  fireEvent.press(view.getByText('Отклонить результаты'));
  await waitFor(() => expect(view.getByText('Нет сети')).toBeTruthy());
  expect(view.getByPlaceholderText('Причина отклонения').props.value).toBe(
    '  Пересчитать имущество  ',
  );
  expect(view.popTo).not.toHaveBeenCalled();
  fireEvent.press(view.getByText('Отклонить результаты'));
  await waitFor(() => expect(view.popTo).toHaveBeenCalled());
  expect(mobileApi.reject).toHaveBeenLastCalledWith(
    'inventory',
    'test-password',
    'Пересчитать имущество',
    3,
  );
});
