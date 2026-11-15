import { Alert, Platform } from 'react-native';
import { appAlert } from '../src/utils/appAlert';

const originalWindow = Object.getOwnPropertyDescriptor(globalThis, 'window');
afterEach(() => {
  jest.restoreAllMocks();
  if (originalWindow) Object.defineProperty(globalThis, 'window', originalWindow);
  else Reflect.deleteProperty(globalThis, 'window');
});
it('keeps native alerts unchanged', () => {
  jest.replaceProperty(Platform, 'OS', 'android');
  const alert = jest.spyOn(Alert, 'alert').mockImplementation(() => {});
  appAlert('Title', 'Message');
  expect(alert).toHaveBeenCalledWith('Title', 'Message', undefined, undefined);
});
it('shows web result alerts and invokes their closing callback', () => {
  jest.replaceProperty(Platform, 'OS', 'web');
  const alert = jest.fn(); const close = jest.fn();
  Object.defineProperty(globalThis, 'window', { configurable: true, value: { alert } });
  appAlert('Результат', 'Неизвестный QR', [{ text: 'Закрыть', onPress: close }]);
  expect(alert).toHaveBeenCalledWith('Результат\n\nНеизвестный QR');
  expect(close).toHaveBeenCalledTimes(1);
});
it.each([true, false])('respects web confirmation decision %s', accepted => {
  jest.replaceProperty(Platform, 'OS', 'web');
  const confirm = jest.fn(() => accepted); const ok = jest.fn(); const cancel = jest.fn();
  Object.defineProperty(globalThis, 'window', { configurable: true, value: { confirm } });
  appAlert('Завершить?', 'Остаток станет ненайденным', [{ text: 'Отмена', style: 'cancel', onPress: cancel }, { text: 'Завершить', onPress: ok }]);
  expect(ok).toHaveBeenCalledTimes(accepted ? 1 : 0);
  expect(cancel).toHaveBeenCalledTimes(accepted ? 0 : 1);
});
