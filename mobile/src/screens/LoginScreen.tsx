import React, { useRef, useState } from 'react';
import {
  Button,
  Platform,
  StyleSheet,
  Text,
  TextInput,
  View,
} from 'react-native';
import { StackScreenProps } from '@react-navigation/stack';
import { RootStackParamList } from '../navigation/types';
import { mobileApi } from '../api/mobileApi';
import { session } from '../auth/session';
import { withSessionLock } from '../auth/sessionPlatform';

type Props = StackScreenProps<RootStackParamList, 'Login'> & {
  onSignedIn: () => void;
};

export function LoginScreen({ onSignedIn }: Props) {
  const passwordInput = useRef<TextInput>(null);
  const pending = useRef(false);
  const [login, setLogin] = useState('');
  const [password, setPassword] = useState('');
  const [loading, setLoading] = useState(false);
  const [errorMessage, setErrorMessage] = useState('');
  const submit = async () => {
    if (pending.current) return;
    setErrorMessage('');
    if (!login.trim() || !password) {
      setErrorMessage('Введите логин и пароль.');
      return;
    }
    pending.current = true;
    setLoading(true);
    const revision = session.getRevision();
    try {
      const tokens = await mobileApi.login(login.trim(), password, Platform.OS);
      if (tokens.user.role !== 'InventoryEmployee') {
        await mobileApi.logout(tokens.refreshToken).catch(() => undefined);
        throw new Error('Мобильное приложение доступно только инвентаризатору. Для администратора используйте веб-панель.');
      }
      await withSessionLock(() => session.setTokens(tokens, revision));
      onSignedIn();
    } catch (error) {
      setErrorMessage(
        error instanceof Error ? error.message : 'Повторите попытку.',
      );
    } finally {
      pending.current = false;
      setLoading(false);
    }
  };
  return (
    <View style={styles.container}>
      <Text style={styles.title}>Asset Inventory</Text>
      {errorMessage ? (
        <Text accessibilityRole="alert" style={styles.error}>
          {errorMessage}
        </Text>
      ) : null}
      <TextInput
        style={styles.input}
        placeholder="Логин"
        autoCapitalize="none"
        value={login}
        editable={!loading}
        onChangeText={setLogin}
        returnKeyType="next"
        onSubmitEditing={() => passwordInput.current?.focus()}
      />
      <TextInput
        ref={passwordInput}
        style={styles.input}
        placeholder="Пароль"
        secureTextEntry
        value={password}
        editable={!loading}
        onChangeText={setPassword}
        returnKeyType="done"
        onSubmitEditing={submit}
      />
      <Button
        title={loading ? 'Вход...' : 'Войти'}
        disabled={loading}
        onPress={submit}
      />
    </View>
  );
}
const styles = StyleSheet.create({
  error: { color: '#a02020' },
  container: { flex: 1, justifyContent: 'center', padding: 24, gap: 14 },
  title: {
    fontSize: 26,
    fontWeight: '700',
    textAlign: 'center',
    marginBottom: 20,
  },
  input: {
    borderWidth: 1,
    borderColor: '#c8c8c8',
    borderRadius: 8,
    padding: 12,
    fontSize: 16,
  },
});
