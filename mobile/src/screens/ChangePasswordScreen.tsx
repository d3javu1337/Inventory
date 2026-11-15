import React, { useRef, useState } from 'react';
import { Button, Platform, ScrollView, Text, TextInput, View } from 'react-native';
import { StackScreenProps } from '@react-navigation/stack';
import { RootStackParamList } from '../navigation/types';
import { mobileApi } from '../api/mobileApi';
import { session } from '../auth/session';
import { withSessionLock } from '../auth/sessionPlatform';
import { ui } from '../ui/kit';

type Props = StackScreenProps<RootStackParamList, 'ChangePassword'>;

export function ChangePasswordScreen({ navigation }: Props) {
  const [current, setCurrent] = useState('');
  const [password, setPassword] = useState('');
  const [confirmation, setConfirmation] = useState('');
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState(false);
  const [error, setError] = useState('');
  const pending = useRef(false);
  const submit = async () => {
    if (pending.current || done) return;
    setError('');
    if (!current) return setError('Введите текущий пароль.');
    if (!password.trim() || password.length < 8 || password.length > 256) return setError('Новый пароль должен содержать от 8 до 256 символов.');
    if (password === current) return setError('Новый пароль должен отличаться от текущего.');
    if (password !== confirmation) return setError('Пароли не совпадают.');
    pending.current = true;
    setBusy(true);
    const revision = session.getRevision();
    try {
      const tokens = await mobileApi.changePassword(current, password, Platform.OS);
      await withSessionLock(() => session.setTokens(tokens, revision));
      setCurrent(''); setPassword(''); setConfirmation('');
      setDone(true);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось изменить пароль. Повторите попытку.');
    } finally { pending.current = false; setBusy(false); }
  };
  return <ScrollView style={ui.screen} contentContainerStyle={ui.content} keyboardShouldPersistTaps="handled">
    <View style={ui.card}>
      <Text style={ui.heading}>Смена пароля</Text>
      {done ? <>
        <Text accessibilityRole="alert" style={ui.text}>Пароль изменён. На остальных устройствах потребуется войти заново.</Text>
        <Button title="К инвентаризациям" onPress={() => navigation.goBack()} />
      </> : <>
        <Text style={ui.muted}>После смены пароля остальные сеансы будут завершены.</Text>
        {error ? <Text accessibilityRole="alert" style={ui.error}>{error}</Text> : null}
        <TextInput accessibilityLabel="Текущий пароль" placeholder="Текущий пароль" style={ui.input} secureTextEntry autoCapitalize="none" autoCorrect={false} autoComplete="current-password" value={current} editable={!busy} onChangeText={setCurrent} />
        <TextInput accessibilityLabel="Новый пароль" placeholder="Новый пароль" style={ui.input} secureTextEntry autoCapitalize="none" autoCorrect={false} autoComplete="new-password" maxLength={256} value={password} editable={!busy} onChangeText={setPassword} />
        <TextInput accessibilityLabel="Повторите новый пароль" placeholder="Повторите новый пароль" style={ui.input} secureTextEntry autoCapitalize="none" autoCorrect={false} autoComplete="new-password" maxLength={256} value={confirmation} editable={!busy} onChangeText={setConfirmation} returnKeyType="done" onSubmitEditing={submit} />
        <Button title={busy ? 'Сохранение…' : 'Изменить пароль'} disabled={busy} onPress={submit} />
      </>}
    </View>
  </ScrollView>;
}
