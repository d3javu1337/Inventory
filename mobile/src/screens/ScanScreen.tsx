import React, { useEffect, useRef, useState } from 'react';
import { AppState, Button, Linking, StyleSheet, Text, TextInput, View } from 'react-native';
import { Camera, CameraType } from 'react-native-camera-kit';
import { StackScreenProps } from '@react-navigation/stack';
import { RootStackParamList } from '../navigation/types';
import { isUuid } from '../utils/inventory';
import { checkCameraPermission, requestCameraPermission } from '../utils/cameraPermission';

type Props = StackScreenProps<RootStackParamList, 'Scan'>;

export function ScanScreen({ route, navigation }: Props) {
  const [manual, setManual] = useState('');
  const [cameraVisible, setCameraVisible] = useState(true);
  const [permission, setPermission] = useState<boolean | null>(null);
  const submitted = useRef(false);
  const requestPermission = async () => {
    try { setPermission(await requestCameraPermission()); }
    catch { setPermission(false); }
  };
  useEffect(() => { requestPermission(); }, []);
  useEffect(() => {
    let alive = true;
    const subscription = AppState.addEventListener('change', state => {
      if (state !== 'active') return;
      // Settings can grant or revoke access. Check silently, without reopening a dialog.
      checkCameraPermission().then(
        granted => { if (alive) setPermission(granted); },
        () => { if (alive) setPermission(false); },
      );
    });
    return () => { alive = false; subscription.remove(); };
  }, []);
  const submit = (qrValue: string) => { if (submitted.current) return; submitted.current = true; setCameraVisible(false); navigation.replace('AssetResult', { ...route.params, qrValue }); };
  return <View style={styles.container}>{!cameraVisible ? <Text style={styles.processing}>Открытие карточки…</Text> : permission ? <Camera style={styles.camera} cameraType={CameraType.Back} scanBarcode onReadCode={(event) => event.nativeEvent.codeStringValue && submit(event.nativeEvent.codeStringValue)} /> : <View style={styles.camera}><Text>{permission === null ? 'Запрос разрешения камеры…' : 'Для сканирования разрешите доступ к камере.'}</Text>{permission === false ? <><Button title="Запросить разрешение" onPress={requestPermission} /><Button title="Открыть настройки" onPress={() => Linking.openSettings()} /></> : null}</View>}{__DEV__ ? <><Text style={styles.debugTitle}>Debug: ручной UUID</Text><TextInput style={styles.input} value={manual} onChangeText={setManual} placeholder="UUIDv7" autoCapitalize="none" /><Button title="Ввести UUID" disabled={!isUuid(manual)} onPress={() => submit(manual.trim())} /></> : null}</View>;
}
const styles = StyleSheet.create({ container: { flex: 1, padding: 12, gap: 12 }, camera: { flex: 1, minHeight: 320 }, processing: { flex: 1, textAlign: 'center', paddingTop: 120, fontSize: 18 }, debugTitle: { fontWeight: '700' }, input: { borderWidth: 1, borderColor: '#ccc', borderRadius: 8, padding: 12 } });
