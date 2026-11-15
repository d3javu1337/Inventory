import React from 'react';
import { act, fireEvent, render, waitFor } from '@testing-library/react-native';
import { ChangePasswordScreen } from '../src/screens/ChangePasswordScreen';
import { mobileApi } from '../src/api/mobileApi';
import { session } from '../src/auth/session';

jest.mock('../src/api/mobileApi', () => ({ mobileApi: { changePassword: jest.fn() } }));
jest.mock('../src/auth/session', () => ({ session: { getRevision: () => 0, setTokens: jest.fn() } }));
beforeEach(() => jest.clearAllMocks());
function screen() {
  const goBack = jest.fn();
  const props = { navigation: { goBack } } as unknown as React.ComponentProps<typeof ChangePasswordScreen>;
  return { ...render(<ChangePasswordScreen {...props} />), goBack };
}
function fill(view: ReturnType<typeof screen>) {
  fireEvent.changeText(view.getByPlaceholderText('Текущий пароль'), 'old-password');
  fireEvent.changeText(view.getByPlaceholderText('Новый пароль'), 'new-password');
  fireEvent.changeText(view.getByPlaceholderText('Повторите новый пароль'), 'new-password');
}
it('validates before keyboard submission', () => {
  const view = screen();
  fireEvent(view.getByPlaceholderText('Повторите новый пароль'), 'submitEditing');
  expect(view.getByText('Введите текущий пароль.')).toBeTruthy();
  fill(view);
  fireEvent.changeText(view.getByPlaceholderText('Повторите новый пароль'), 'different-password');
  fireEvent.press(view.getByText('Изменить пароль'));
  expect(view.getByText('Пароли не совпадают.')).toBeTruthy();
  expect(mobileApi.changePassword).not.toHaveBeenCalled();
});
it('keeps inputs on failure and accepts a retry with the new session', async () => {
  const tokens = { accessToken: 'new-access' } as Awaited<ReturnType<typeof mobileApi.changePassword>>;
  jest.mocked(mobileApi.changePassword).mockRejectedValueOnce(new Error('Неверный текущий пароль.')).mockResolvedValueOnce(tokens);
  const view = screen();
  fill(view);
  fireEvent.press(view.getByText('Изменить пароль'));
  await waitFor(() => expect(view.getByText('Неверный текущий пароль.')).toBeTruthy());
  expect(view.getByPlaceholderText('Новый пароль').props.value).toBe('new-password');
  expect(session.setTokens).not.toHaveBeenCalled();
  fireEvent.changeText(view.getByPlaceholderText('Текущий пароль'), 'correct-password');
  fireEvent.press(view.getByText('Изменить пароль'));
  await waitFor(() => expect(view.getByText('Пароль изменён. На остальных устройствах потребуется войти заново.')).toBeTruthy());
  expect(mobileApi.changePassword).toHaveBeenLastCalledWith('correct-password', 'new-password', expect.any(String));
  expect(session.setTokens).toHaveBeenCalledWith(tokens, 0);
  fireEvent.press(view.getByText('К инвентаризациям'));
  expect(view.goBack).toHaveBeenCalledTimes(1);
});
it('sends one request when Enter is submitted twice before render', async () => {
  let resolve!: (tokens: Awaited<ReturnType<typeof mobileApi.changePassword>>) => void;
  jest.mocked(mobileApi.changePassword).mockReturnValue(new Promise(done => { resolve = done; }));
  const view = screen();
  fill(view);
  act(() => {
    fireEvent(view.getByPlaceholderText('Повторите новый пароль'), 'submitEditing');
    fireEvent(view.getByPlaceholderText('Повторите новый пароль'), 'submitEditing');
  });
  expect(mobileApi.changePassword).toHaveBeenCalledTimes(1);
  await act(async () => resolve({} as Awaited<ReturnType<typeof mobileApi.changePassword>>));
  expect(session.setTokens).toHaveBeenCalledTimes(1);
});
