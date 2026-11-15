import React from 'react';
import { AppState, PermissionsAndroid, Platform } from 'react-native';
import { act, fireEvent, render, waitFor } from '@testing-library/react-native';
import { Camera } from 'react-native-camera-kit';
import { ScanScreen } from '../src/screens/ScanScreen';

jest.mock('react-native-camera-kit', () => ({ Camera: jest.fn(() => null), CameraType: { Back: 'back' } }));
describe('Android QR camera', () => {
  beforeEach(() => { jest.clearAllMocks(); jest.replaceProperty(Platform, 'OS', 'android'); });
  afterEach(() => jest.restoreAllMocks());
  function screen() {
    const replace = jest.fn();
    const props = { route: { params: { id: 'inventory', round: 3 } }, navigation: { replace } } as unknown as React.ComponentProps<typeof ScanScreen>;
    return { ...render(<ScanScreen {...props} />), replace };
  }
  it('does not mount a camera without permission and offers retry', async () => {
    const permission = jest.spyOn(PermissionsAndroid, 'request').mockResolvedValue(PermissionsAndroid.RESULTS.DENIED);
    const ui = screen();
    await waitFor(() => expect(ui.getByText('Для сканирования разрешите доступ к камере.')).toBeTruthy());
    expect(Camera).not.toHaveBeenCalled();
    permission.mockResolvedValue(PermissionsAndroid.RESULTS.GRANTED);
    fireEvent.press(ui.getByText(/запросить разрешение/i));
    await waitFor(() => expect(Camera).toHaveBeenCalled());
  });
  it('accepts only one callback even if the camera reads the QR twice', async () => {
    jest.spyOn(PermissionsAndroid, 'request').mockResolvedValue(PermissionsAndroid.RESULTS.GRANTED);
    const ui = screen();
    await waitFor(() => expect(Camera).toHaveBeenCalled());
    const props = (Camera as unknown as jest.Mock).mock.calls[0][0];
    act(() => { props.onReadCode({ nativeEvent: { codeStringValue: 'test-qr' } }); props.onReadCode({ nativeEvent: { codeStringValue: 'test-qr' } }); });
    expect(ui.replace).toHaveBeenCalledTimes(1);
    expect(ui.replace).toHaveBeenCalledWith('AssetResult', { id: 'inventory', qrValue: 'test-qr', round: 3 });
  });
  it('updates camera access when returning from settings without another prompt', async () => {
    const request = jest.spyOn(PermissionsAndroid, 'request').mockResolvedValue(PermissionsAndroid.RESULTS.DENIED);
    const check = jest.spyOn(PermissionsAndroid, 'check').mockResolvedValue(true);
    const remove = jest.fn();
    const subscribe = jest.spyOn(AppState, 'addEventListener').mockReturnValue({ remove });
    const ui = screen();
    await waitFor(() => expect(ui.getByText('Для сканирования разрешите доступ к камере.')).toBeTruthy());
    const resume = subscribe.mock.calls[0][1];
    await act(async () => { resume('active'); });
    expect(Camera).toHaveBeenCalled();
    expect(request).toHaveBeenCalledTimes(1);
    check.mockResolvedValue(false);
    await act(async () => { resume('active'); });
    expect(ui.getByText('Для сканирования разрешите доступ к камере.')).toBeTruthy();
    ui.unmount();
    expect(remove).toHaveBeenCalledTimes(1);
  });
  it('keeps debug UUID entry available when camera permission is denied', async () => {
    jest.spyOn(PermissionsAndroid, 'request').mockResolvedValue(PermissionsAndroid.RESULTS.DENIED);
    const ui = screen();
    await waitFor(() => expect(ui.getByText(/запросить разрешение/i)).toBeTruthy());
    fireEvent.changeText(ui.getByPlaceholderText('UUIDv7'), '01992e83-1234-7000-8000-123456789abc');
    fireEvent.press(ui.getByText(/ввести UUID/i));
    expect(ui.replace).toHaveBeenCalledTimes(1);
  });
});
