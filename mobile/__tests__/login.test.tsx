import React from 'react';
import { act, fireEvent, render, waitFor } from '@testing-library/react-native';
import { LoginScreen } from '../src/screens/LoginScreen';
import { mobileApi } from '../src/api/mobileApi';
import { session } from '../src/auth/session';
jest.mock('../src/api/mobileApi', () => ({ mobileApi: { login: jest.fn(), logout: jest.fn().mockResolvedValue(undefined) } }));
jest.mock('../src/auth/session', () => ({ session: { getRevision: () => 0, setTokens: jest.fn() } }));
beforeEach(() => jest.clearAllMocks());
it('shows missing credentials without relying on native alerts', () => {
  const props = { onSignedIn: jest.fn() } as unknown as React.ComponentProps<
    typeof LoginScreen
  >;
  const ui = render(<LoginScreen {...props} />);
  fireEvent.press(ui.getByText('Войти'));
  expect(ui.getByText('Введите логин и пароль.')).toBeTruthy();
});
it('shows the server login error inline', async () => {
  jest
    .mocked(mobileApi.login)
    .mockRejectedValue(new Error('Неверные учетные данные.'));
  const props = { onSignedIn: jest.fn() } as unknown as React.ComponentProps<
    typeof LoginScreen
  >;
  const ui = render(<LoginScreen {...props} />);
  fireEvent.changeText(ui.getByPlaceholderText('Логин'), 'test');
  fireEvent.changeText(ui.getByPlaceholderText('Пароль'), 'bad');
  fireEvent.press(ui.getByText('Войти'));
  await waitFor(() =>
    expect(ui.getByText('Неверные учетные данные.')).toBeTruthy(),
  );
});
it('does not send a second login via Enter before the next render', async () => {
  let resolve!: (value: Awaited<ReturnType<typeof mobileApi.login>>) => void;
  jest.mocked(mobileApi.login).mockReturnValue(
    new Promise(done => {
      resolve = done;
    }),
  );
  const onSignedIn = jest.fn();
  const props = { onSignedIn } as unknown as React.ComponentProps<
    typeof LoginScreen
  >;
  const view = render(<LoginScreen {...props} />);
  fireEvent.changeText(view.getByPlaceholderText('Логин'), 'worker');
  fireEvent.changeText(view.getByPlaceholderText('Пароль'), 'test-password');
  act(() => {
    fireEvent(view.getByPlaceholderText('Пароль'), 'submitEditing');
    fireEvent(view.getByPlaceholderText('Пароль'), 'submitEditing');
  });
  expect(mobileApi.login).toHaveBeenCalledTimes(1);
  await act(async () =>
    resolve({ user: { id: 'worker', role: 'InventoryEmployee' } } as Awaited<ReturnType<typeof mobileApi.login>>),
  );
  await waitFor(() => expect(onSignedIn).toHaveBeenCalledTimes(1));
});
it('rejects administrator credentials without storing them as a mobile session', async () => {
  jest.mocked(mobileApi.login).mockResolvedValue({ user: { id: 'admin', role: 'Administrator' }, refreshToken: 'admin-refresh' } as Awaited<ReturnType<typeof mobileApi.login>>);
  const onSignedIn = jest.fn();
  const props = { onSignedIn } as unknown as React.ComponentProps<typeof LoginScreen>;
  const view = render(<LoginScreen {...props} />);
  fireEvent.changeText(view.getByPlaceholderText('Логин'), 'admin');
  fireEvent.changeText(view.getByPlaceholderText('Пароль'), 'password-123');
  fireEvent.press(view.getByText('Войти'));
  await waitFor(() => expect(view.getByText(/Мобильное приложение доступно только инвентаризатору/)).toBeTruthy());
  expect(mobileApi.logout).toHaveBeenCalledWith('admin-refresh');
  expect(session.setTokens).not.toHaveBeenCalled();
  expect(onSignedIn).not.toHaveBeenCalled();
});
