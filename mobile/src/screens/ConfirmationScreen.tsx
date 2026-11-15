import React, { useRef, useState } from 'react';
import { ScrollView, Text, TextInput, View } from 'react-native';
import { StackScreenProps } from '@react-navigation/stack';
import { RootStackParamList } from '../navigation/types';
import { mobileApi } from '../api/mobileApi';
import { queryClient } from '../query/QueryProvider';
import { Action, ui } from '../ui/kit';

type Props = StackScreenProps<RootStackParamList, 'Confirmation'>;
export function ConfirmationScreen({ route, navigation }: Props) {
  const [password, setPassword] = useState('');
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const pending = useRef(false);
  const submit = async (decision: 'confirm' | 'reject') => {
    // Keyboard events can bypass a disabled button, including in the same render.
    if (pending.current) return;
    if (!password) {
      setError('Введите пароль для подтверждения действия.');
      return;
    }
    if (decision === 'reject' && !reason.trim()) {
      setError('Укажите причину отклонения.');
      return;
    }
    if (decision === 'reject' && reason.trim().length > 1000) {
      setError('Причина отклонения не может быть длиннее 1000 символов.');
      return;
    }
    pending.current = true;
    setBusy(true);
    setError('');
    try {
      if (decision === 'confirm')
        await mobileApi.confirm(route.params.id, password, route.params.round);
      else await mobileApi.reject(route.params.id, password, reason.trim(), route.params.round);
      setPassword('');
      await queryClient.invalidateQueries({
        queryKey: ['mobile-inventory', route.params.id],
      });
      await queryClient.invalidateQueries({ queryKey: ['mobile-inventories'] });
      navigation.popTo('Inventory', { id: route.params.id });
    } catch (failure) {
      setError(
        failure instanceof Error
          ? failure.message
          : 'Не удалось сохранить решение. Повторите попытку.',
      );
    } finally {
      pending.current = false;
      setBusy(false);
    }
  };
  return (
    <ScrollView
      style={ui.screen}
      contentContainerStyle={ui.content}
      keyboardShouldPersistTaps="handled"
    >
      <Text style={ui.title}>Итоги проверки</Text>
      <View style={ui.card}>
        <Text style={ui.heading}>Подтверждение результатов</Text>
        <Text style={ui.text}>
          Подтверждение завершит инвентаризацию и зафиксирует отчёты. Если
          результаты нужно исправить, отклоните их с указанием причины.
        </Text>
        <Text style={ui.muted}>
          Используйте тот же пароль, что и для входа. Системное подтверждение не
          заменяет ручную подпись в отчёте.
        </Text>
      </View>
      <Text style={ui.label}>Пароль</Text>
      <TextInput
        accessibilityLabel="Пароль"
        style={ui.input}
        placeholder="Пароль"
        secureTextEntry
        autoCapitalize="none"
        autoCorrect={false}
        editable={!busy}
        value={password}
        onChangeText={value => {
          setPassword(value);
          setError('');
        }}
        onSubmitEditing={() => submit('confirm')}
        returnKeyType="done"
      />
      <Text style={ui.label}>Причина · только для отклонения</Text>
      <TextInput
        accessibilityLabel="Причина отклонения"
        style={[ui.input, ui.comment]}
        placeholder="Причина отклонения"
        editable={!busy}
        value={reason}
        onChangeText={value => {
          setReason(value);
          setError('');
        }}
        maxLength={1000}
        multiline
      />
      {error ? (
        <Text accessibilityRole="alert" style={ui.error}>
          {error}
        </Text>
      ) : null}
      <Action
        title={busy ? 'Сохранение…' : 'Подтвердить результаты'}
        disabled={busy || !password}
        onPress={() => submit('confirm')}
      />
      <Action
        title="Отклонить результаты"
        secondary
        disabled={busy || !password}
        onPress={() => submit('reject')}
      />
    </ScrollView>
  );
}
