import React, { useRef, useState } from 'react';
import {
  ActivityIndicator,
  ScrollView,
  Text,
  TextInput,
  View,
} from 'react-native';
import { StackScreenProps } from '@react-navigation/stack';
import { useQuery } from '@tanstack/react-query';
import { RootStackParamList } from '../navigation/types';
import { mobileApi } from '../api/mobileApi';
import { queryClient } from '../query/QueryProvider';
import { assetConditions, requiresComment } from '../utils/inventory';
import { appAlert } from '../utils/appAlert';
import { Action, Chip, ui } from '../ui/kit';

type Props = StackScreenProps<RootStackParamList, 'AssetResult'>;
export function AssetResultScreen({ route, navigation }: Props) {
  const query = useQuery({
    queryKey: [
      'mobile-inventory',
      route.params.id,
      'asset',
      route.params.qrValue,
    ],
    queryFn: () =>
      mobileApi.inventory(route.params.id, 1, 'all', route.params.qrValue),
  });
  const [condition, setCondition] = useState('Serviceable');
  const [comment, setComment] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const pending = useRef(false);
  const asset = query.data?.items.find(
    item => item.assetId.toLowerCase() === route.params.qrValue.toLowerCase(),
  );
  const submit = async () => {
    if (pending.current) return;
    if (requiresComment(condition) && !comment.trim()) {
      setError('Опишите повреждение или причину непригодности.');
      return;
    }
    pending.current = true;
    setBusy(true);
    setError('');
    try {
      const result = await mobileApi.scan(route.params.id, {
        qrValue: route.params.qrValue,
        condition,
        comment: comment.trim() || null,
      }, route.params.round);
      await queryClient.invalidateQueries({
        queryKey: ['mobile-inventory', route.params.id],
      });
      await queryClient.invalidateQueries({ queryKey: ['mobile-inventories'] });
      if (result.duplicate || result.result !== 'Found') {
        appAlert(
          result.result === 'Found' ? 'Уже найдено' : 'Посторонний QR',
          result.message,
          [
            {
              text: 'Закрыть',
              onPress: () =>
                navigation.popTo('Inventory', { id: route.params.id }),
            },
          ],
        );
      } else navigation.popTo('Inventory', { id: route.params.id });
    } catch (failure) {
      setError(
        failure instanceof Error
          ? failure.message
          : 'Не удалось сохранить. Повторите попытку.',
      );
    } finally {
      pending.current = false;
      setBusy(false);
    }
  };
  if (query.isLoading) return <ActivityIndicator />;
  if (!query.data)
    return (
      <View style={ui.content}>
        <Text style={ui.error}>Не удалось загрузить состав проверки.</Text>
        <Action title="Повторить" onPress={() => query.refetch()} />
      </View>
    );
  return (
    <ScrollView
      style={ui.screen}
      contentContainerStyle={ui.content}
      keyboardShouldPersistTaps="handled"
    >
      <Text style={ui.title}>
        {asset ? 'Устройство найдено' : 'Проверка QR'}
      </Text>
      <Text style={ui.muted}>
        Результат появится в проверке только после сохранения.
      </Text>
      <View style={ui.card}>
        {asset ? (
          <>
            <Text style={ui.label}>{asset.inventoryNumber}</Text>
            <Text style={ui.heading}>{asset.name}</Text>
            <Text style={ui.text}>
              {asset.departmentName} · {asset.locationName}
            </Text>
            <Text style={ui.muted}>
              МОЛ: {asset.materiallyResponsibleFullName}
            </Text>
          </>
        ) : (
          <>
            <Text style={ui.text}>
              Объекта нет в составе. Сервер проверит, неизвестен ли код или ОС
              относится к другой проверке.
            </Text>
            <Text selectable style={ui.muted}>
              {route.params.qrValue}
            </Text>
          </>
        )}
      </View>
      <Text style={ui.heading}>Состояние устройства</Text>
      <View style={ui.row}>
        {assetConditions.map(value => (
          <Chip
            key={value.value}
            title={value.label}
            selected={condition === value.value}
            onPress={() => {
              if (!busy) {
                setCondition(value.value);
                setError('');
              }
            }}
          />
        ))}
      </View>
      <Text style={ui.label}>
        {requiresComment(condition)
          ? 'Комментарий · обязательно'
          : 'Комментарий · необязательно'}
      </Text>
      <TextInput
        accessibilityLabel="Комментарий"
        style={[ui.input, ui.comment]}
        placeholder="Комментарий"
        value={comment}
        onChangeText={setComment}
        multiline
        maxLength={2000}
        editable={!busy}
      />
      {error ? (
        <Text accessibilityRole="alert" style={ui.error}>
          {error}
        </Text>
      ) : null}
      <Action
        title={busy ? 'Сохранение…' : 'Сохранить результат'}
        disabled={busy}
        onPress={submit}
      />
    </ScrollView>
  );
}
