import React from 'react';
import { fireEvent, render } from '@testing-library/react-native';
import { InventoryDetailsScreen } from '../src/screens/InventoryDetailsScreen';

const mockInventory = {
  number: 'INV-TEST', status: 'Completed', reason: 'AnnualReporting', unexpectedScans: [],
  commission: [{ id: 'member', userId: 'worker', employeeName: 'Иванов', role: 'Chairperson', confirmationStatus: 'Confirmed' }],
  items: [
    { id: '1', assetId: '01992e83-1234-7000-8000-123456789abc', inventoryNumber: 'OS-1', name: 'Стол', result: 'NotFound', condition: null, comment: null },
    { id: '2', inventoryNumber: 'OS-2', name: 'Монитор', result: 'Found', condition: 'Damaged', comment: 'Царапина' },
  ],
};
jest.mock('@tanstack/react-query', () => ({ useQuery: () => ({ data: mockInventory, isLoading: false, isError: false }) }));
jest.mock('../src/query/QueryProvider', () => ({ queryClient: {} }));
jest.mock('../src/auth/session', () => ({ session: { getUser: () => ({ id: 'worker' }) } }));
jest.mock('../src/api/mobileApi', () => ({ mobileApi: {} }));

it('shows only not-yet-found devices by default and retains found results separately', () => {
  const props = { route: { params: { id: 'inventory' } }, navigation: {} } as unknown as React.ComponentProps<typeof InventoryDetailsScreen>;
  const ui = render(<InventoryDetailsScreen {...props} />);
  expect(ui.getByText('OS-1')).toBeTruthy();
  expect(ui.queryByText('OS-2')).toBeNull();
  fireEvent.press(ui.getByText('Найдено · 1'));
  expect(ui.getByText('OS-2')).toBeTruthy();
  expect(ui.getByText('Найдено · Повреждено')).toBeTruthy();
  expect(ui.getByText('Царапина')).toBeTruthy();
  expect(ui.queryByText('OS-1')).toBeNull();
});
it('searches the selected list without changing the global count', () => {
  const props = { route: { params: { id: 'inventory' } }, navigation: {} } as unknown as React.ComponentProps<typeof InventoryDetailsScreen>;
  const screen = render(<InventoryDetailsScreen {...props} />);
  fireEvent.changeText(screen.getByPlaceholderText('Номер, название или помещение'), 'does-not-exist');
  expect(screen.getByText('Ничего не найдено')).toBeTruthy();
  expect(screen.getByText('1 / 2')).toBeTruthy();
});
it('does not hide a server result when searching by the asset UUID', () => {
  const props = { route: { params: { id: 'inventory' } }, navigation: {} } as unknown as React.ComponentProps<typeof InventoryDetailsScreen>;
  const screen = render(<InventoryDetailsScreen {...props} />);
  fireEvent.changeText(screen.getByPlaceholderText('Номер, название или помещение'), '01992E83-1234-7000-8000-123456789ABC');
  expect(screen.getByText('OS-1')).toBeTruthy();
  expect(screen.queryByText('Ничего не найдено')).toBeNull();
});
it('removes a saved found device from the work list without discarding it', () => {
  const props = { route: { params: { id: 'inventory' } }, navigation: {} } as unknown as React.ComponentProps<typeof InventoryDetailsScreen>;
  const screen = render(<InventoryDetailsScreen {...props} />);
  expect(screen.getByText('OS-1')).toBeTruthy();
  mockInventory.items[0].result = 'Found';
  mockInventory.items[0].condition = 'Serviceable';
  screen.rerender(<InventoryDetailsScreen {...props} />);
  expect(screen.queryByText('OS-1')).toBeNull();
  expect(screen.getByText('Все устройства найдены')).toBeTruthy();
  fireEvent.press(screen.getByText('Найдено · 2'));
  expect(screen.getByText('OS-1')).toBeTruthy();
  mockInventory.items[0].result = 'NotFound';
  mockInventory.items[0].condition = null;
});
