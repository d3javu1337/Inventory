import { act, fireEvent, render } from '@testing-library/react';
import { expect, it, vi } from 'vitest';
import { z } from 'zod';
import { ValidatedForm } from '../src/components/ValidatedForm';

it('serializes submissions before a rerender and permits retry after rejection', async () => {
  let reject!: (error: Error) => void;
  const save = vi.fn(() => new Promise<void>((_, fail) => { reject = fail; }));
  const view = render(<ValidatedForm schema={z.object({ name: z.string() })} value={{ name: 'Отдел' }} onValid={save}><button type="submit">Сохранить</button></ValidatedForm>);
  const form = view.getByRole('button').closest('form')!;
  fireEvent.submit(form); fireEvent.submit(form);
  expect(save).toHaveBeenCalledTimes(1);
  await act(async () => { reject(new Error('offline')); });
  fireEvent.submit(form);
  expect(save).toHaveBeenCalledTimes(2);
  await act(async () => { reject(new Error('offline')); });
});
