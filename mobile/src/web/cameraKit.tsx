import React, { useEffect, useRef, useState } from 'react';
import { StyleProp, StyleSheet, Text, View, ViewStyle } from 'react-native';
import jsQR from 'jsqr';

export const CameraType = { Back: 'back', Front: 'front' } as const;

type Props = {
  style?: StyleProp<ViewStyle>;
  scanBarcode?: boolean;
  onReadCode?: (event: { nativeEvent: { codeStringValue: string } }) => void;
};

export function Camera({ style, scanBarcode, onReadCode }: Props) {
  const video = useRef<HTMLVideoElement>(null);
  const onRead = useRef(onReadCode);
  onRead.current = onReadCode;
  const [cameras, setCameras] = useState<MediaDeviceInfo[]>([]);
  const [selected, setSelected] = useState('');
  const [attempt, setAttempt] = useState(0);
  const [error, setError] = useState('');

  useEffect(() => {
    const element = video.current!;
    const media = navigator.mediaDevices;
    if (!media?.getUserMedia) {
      setError('Откройте приложение через localhost или HTTPS для доступа к камере.');
      return;
    }

    let active = true;
    let stream: MediaStream | undefined;
    let timer: number | undefined;
    setError('');
    const start = async () => {
      const before = (await media.enumerateDevices()).filter(device => device.kind === 'videoinput');
      if (!active) return;
      setCameras(before.filter(device => device.deviceId));
      const obs = before.find(device => /OBS Virtual Camera/i.test(device.label));
      const source = selected || obs?.deviceId;
      const opened = await media.getUserMedia({
        audio: false,
        video: source ? { deviceId: { exact: source } } : { facingMode: { ideal: 'environment' } },
      });
      if (!active) {
        opened.getTracks().forEach(track => track.stop());
        return;
      }
      stream = opened;
      const inputs = (await media.enumerateDevices()).filter(device => device.kind === 'videoinput');
      if (!active) return;
      setCameras(inputs.filter(device => device.deviceId));
      const actual = opened.getVideoTracks()[0].getSettings().deviceId;
      const preferred = inputs.find(device => /OBS Virtual Camera/i.test(device.label));
      if (!selected && preferred && preferred.deviceId !== actual) {
        setSelected(preferred.deviceId);
        return;
      }

      element.srcObject = opened;
      await element.play();
      if (!active || !scanBarcode) return;
      const canvas = document.createElement('canvas');
      const context = canvas.getContext('2d', { willReadFrequently: true });
      if (!context) throw new Error('Canvas is unavailable');
      timer = window.setInterval(() => {
        if (element.readyState < 2 || !element.videoWidth || !element.videoHeight) return;
        const width = Math.min(element.videoWidth, 960);
        const height = Math.max(1, Math.round(width * element.videoHeight / element.videoWidth));
        if (canvas.width !== width || canvas.height !== height) {
          canvas.width = width;
          canvas.height = height;
        }
        context.drawImage(element, 0, 0, width, height);
        const pixels = context.getImageData(0, 0, width, height);
        const qr = jsQR(pixels.data, width, height);
        if (!qr?.data) return;
        window.clearInterval(timer);
        onRead.current?.({ nativeEvent: { codeStringValue: qr.data } });
      }, 180);
    };
    start().catch(failure => {
      if (!active) return;
      stream?.getTracks().forEach(track => track.stop());
      element.srcObject = null;
      setError(failure?.name === 'NotAllowedError'
        ? 'Разрешите браузеру доступ к камере и повторите.'
        : 'Камера недоступна. Запустите виртуальную камеру в OBS или выберите другой источник.');
    });

    return () => {
      active = false;
      window.clearInterval(timer);
      stream?.getTracks().forEach(track => track.stop());
      element.srcObject = null;
    };
  }, [attempt, scanBarcode, selected]);

  return (
    <View style={[styles.container, style]}>
      <label>
        Источник камеры{' '}
        <select value={selected} onChange={event => setSelected(event.target.value)}>
          <option value="">Автоматически</option>
          {cameras.map((camera, index) => (
            <option key={camera.deviceId} value={camera.deviceId}>
              {camera.label || `Камера ${index + 1}`}
            </option>
          ))}
        </select>
      </label>
      {error && <Text accessibilityRole="alert">{error}</Text>}
      {error && <button type="button" onClick={() => setAttempt(value => value + 1)}>Повторить</button>}
      <video ref={video} aria-label="Изображение с камеры" playsInline muted style={videoStyle} />
    </View>
  );
}

const styles = StyleSheet.create({
  container: { gap: 8 },
});

const videoStyle: React.CSSProperties = {
  width: '100%',
  minHeight: 200,
  flex: 1,
  objectFit: 'contain',
  backgroundColor: '#111',
};
