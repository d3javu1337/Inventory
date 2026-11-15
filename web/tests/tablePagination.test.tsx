import { fireEvent, render, screen } from '@testing-library/react';
import { TableCell, TableRow } from '@mui/material';
import { expect, it } from 'vitest';
import { DataTable } from '../src/components/Ui';
it('bounds a large local preview to 50 rows and opens the last page', () => {
  render(<DataTable headers={['Ошибка']}>{Array.from({ length: 105 }, (_, index) => <TableRow key={index}><TableCell>Строка-{index + 1}</TableCell></TableRow>)}</DataTable>);
  expect(screen.getByText('Строка-1')).toBeInTheDocument();
  expect(screen.queryByText('Строка-51')).toBeNull();
  fireEvent.click(screen.getByRole('button', { name: 'Следующая страница' }));
  expect(screen.getByText('Строка-51')).toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'Следующая страница' }));
  expect(screen.getByText('Строка-105')).toBeInTheDocument();
  expect(screen.queryByText('Строка-51')).toBeNull();
});
