import React from 'react';
import { act, fireEvent, render, waitFor } from '@testing-library/react-native';
import { InventoryReports } from '../src/components/InventoryReports';
import { downloadInventoryReport } from '../src/files/inventoryReport';

jest.mock('../src/files/inventoryReport', () => ({ downloadInventoryReport: jest.fn() }));
beforeEach(() => jest.clearAllMocks());
it('shows file errors and allows retry in another format', async () => {
  jest.mocked(downloadInventoryReport).mockRejectedValueOnce(new Error('Сохраненный отчет отсутствует.')).mockResolvedValueOnce(true);
  const view = render(<InventoryReports id="inventory" number="INV-1" />);
  fireEvent.press(view.getByText('Сохранить PDF'));
  await waitFor(() => expect(view.getByText('Сохраненный отчет отсутствует.')).toBeTruthy());
  fireEvent.press(view.getByText('Сохранить XLSX'));
  await waitFor(() => expect(view.queryByText('Сохраненный отчет отсутствует.')).toBeNull());
  expect(downloadInventoryReport).toHaveBeenLastCalledWith('inventory', 'INV-1', 'xlsx');
});
it('does not report success when the system save dialog is cancelled', async () => {
  jest.mocked(downloadInventoryReport).mockResolvedValue(false);
  const view = render(<InventoryReports id="inventory" number="INV-1" />);
  fireEvent.press(view.getByText('Сохранить PDF'));
  await waitFor(() => expect(view.getByText('Сохранить PDF')).toBeTruthy());
  expect(view.queryByRole('alert')).toBeNull();
});
it('sends one request when formats are pressed before the next render', async () => {
  let finish!: (saved: boolean) => void;
  jest.mocked(downloadInventoryReport).mockReturnValue(new Promise(done => { finish = done; }));
  const view = render(<InventoryReports id="inventory" number="INV-1" />);
  act(() => { fireEvent.press(view.getByText('Сохранить PDF')); fireEvent.press(view.getByText('Сохранить XLSX')); });
  expect(downloadInventoryReport).toHaveBeenCalledTimes(1);
  await act(async () => finish(true));
});
