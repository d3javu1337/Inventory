import React from 'react';
import { act, fireEvent, render, waitFor } from '@testing-library/react-native';
import { AssetResultScreen } from '../src/screens/AssetResultScreen';
import { mobileApi } from '../src/api/mobileApi';
import { Action } from '../src/ui/kit';
import { appAlert } from '../src/utils/appAlert';
const mockQuery = {
  data: {
    round: 4,
    items: [{ assetId: 'asset-id', inventoryNumber: 'OS-1', name: 'Монитор' }],
  },
  isLoading: false,
};
jest.mock('@tanstack/react-query', () => ({ useQuery: () => mockQuery }));
jest.mock('../src/query/QueryProvider', () => ({
  queryClient: { invalidateQueries: jest.fn().mockResolvedValue(undefined) },
}));
jest.mock('../src/api/mobileApi', () => ({ mobileApi: { scan: jest.fn() } }));
jest.mock('../src/utils/appAlert', () => ({ appAlert: jest.fn() }));
beforeEach(() => jest.clearAllMocks());
function screen() {
  const popTo = jest.fn();
  const props = {
    route: { params: { id: 'inventory', qrValue: 'asset-id', round: 3 } },
    navigation: { popTo },
  } as unknown as React.ComponentProps<typeof AssetResultScreen>;
  return { ...render(<AssetResultScreen {...props} />), popTo };
}
it('validates damaged condition inline before sending a scan', () => {
  const screenUi = screen();
  fireEvent.press(screenUi.getByText('Повреждено'));
  fireEvent.press(screenUi.getByText('Сохранить результат'));
  expect(
    screenUi.getByText('Опишите повреждение или причину непригодности.'),
  ).toBeTruthy();
  expect(mobileApi.scan).not.toHaveBeenCalled();
});
it('returns to the inventory only after saving and refreshing results', async () => {
  jest
    .mocked(mobileApi.scan)
    .mockResolvedValue({
      result: 'Found',
      duplicate: false,
      asset: null,
      message: 'Найдено',
    });
  const screenUi = screen();
  fireEvent.press(screenUi.getByText('Сохранить результат'));
  await waitFor(() =>
    expect(screenUi.popTo).toHaveBeenCalledWith('Inventory', {
      id: 'inventory',
    }),
  );
  expect(mobileApi.scan).toHaveBeenCalledWith('inventory', { qrValue: 'asset-id', condition: 'Serviceable', comment: null }, 3);
});
it('keeps a repeated out-of-scope code separate from a found asset', async () => {
  jest.mocked(mobileApi.scan).mockResolvedValue({
    result: 'OutOfScope', duplicate: true, asset: null, message: 'Этот QR уже отмечен как посторонний.',
  });
  const view = screen();
  fireEvent.press(view.getByText('Сохранить результат'));
  await waitFor(() => expect(appAlert).toHaveBeenCalledWith('Посторонний QR', 'Этот QR уже отмечен как посторонний.', expect.any(Array)));
});
it('preserves input and does not leave the form on a network error', async () => {
  jest.mocked(mobileApi.scan).mockRejectedValue(new Error('Нет сети'));
  const screenUi = screen();
  fireEvent.changeText(
    screenUi.getByPlaceholderText('Комментарий'),
    'Повторить позже',
  );
  fireEvent.press(screenUi.getByText('Сохранить результат'));
  await waitFor(() => expect(screenUi.getByText('Нет сети')).toBeTruthy());
  expect(screenUi.getByPlaceholderText('Комментарий').props.value).toBe(
    'Повторить позже',
  );
  expect(screenUi.popTo).not.toHaveBeenCalled();
});
it('sends only one scan when submission is triggered twice before the next render', async () => {
  let resolveScan: (
    value: Awaited<ReturnType<typeof mobileApi.scan>>,
  ) => void = () => {};
  jest.mocked(mobileApi.scan).mockImplementation(
    () =>
      new Promise(resolve => {
        resolveScan = resolve;
      }),
  );
  const screenUi = screen();
  const submit = screenUi
    .UNSAFE_getAllByType(Action)
    .find(button => button.props.title === 'Сохранить результат')!.props
    .onPress;
  act(() => {
    submit();
    submit();
  });
  expect(mobileApi.scan).toHaveBeenCalledTimes(1);
  await act(async () =>
    resolveScan({
      result: 'Found',
      duplicate: false,
      asset: null,
      message: 'Найдено',
    }),
  );
  await waitFor(() => expect(screenUi.popTo).toHaveBeenCalledTimes(1));
});
