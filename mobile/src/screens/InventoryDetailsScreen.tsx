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
import {
  canConfirm,
  confirmationStatusLabel,
  inventoryReasonLabel,
  inventoryStatusLabel,
  unexpectedScanTypeLabel,
} from '../utils/inventory';
import { queryClient } from '../query/QueryProvider';
import { session } from '../auth/session';
import { Action, Chip, Progress, ui } from '../ui/kit';
import { InventoryReports } from '../components/InventoryReports';

type Props = StackScreenProps<RootStackParamList, 'Inventory'>;
type Filter = 'remaining' | 'found' | 'unexpected';
const PAGE_SIZE = 20;
export function InventoryDetailsScreen({ route, navigation }: Props) {
  const { id } = route.params;
  const [filter, setFilter] = useState<Filter>('remaining');
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(1);
  const [finishing, setFinishing] = useState<{ round?: number } | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const finishPending = useRef(false);
  const query = useQuery({
    queryKey: ['mobile-inventory', id, filter, search, page],
    queryFn: () => mobileApi.inventory(id, page, filter, search),
    placeholderData: (previous, previousQuery) =>
      previousQuery?.queryKey[1] === id ? previous : undefined,
  });
  if (query.isLoading)
    return (
      <View style={[ui.screen, ui.content]}>
        <ActivityIndicator />
        <Text>Загрузка проверки…</Text>
      </View>
    );
  if (!query.data)
    return (
      <View style={[ui.screen, ui.content]}>
        <Text style={ui.error}>Не удалось загрузить проверку.</Text>
        <Action title="Повторить" onPress={() => query.refetch()} />
      </View>
    );
  const inventory = query.data;
  const assignee = inventory.assignee ?? inventory.commission[0];
  const member = assignee?.userId === session.getUser()?.id ? assignee : undefined;
  const found =
    inventory.counts?.found ??
    inventory.items.filter(item => item.result === 'Found').length;
  const pending =
    inventory.counts?.pending ??
    inventory.items.filter(item => item.result === 'Pending').length;
  const missing =
    inventory.counts?.notFound ??
    inventory.items.filter(item => item.result === 'NotFound').length;
  const damaged =
    inventory.counts?.damaged ??
    inventory.items.filter(
      item =>
        item.result === 'Found' &&
        (item.condition === 'Damaged' || item.condition === 'Unusable'),
    ).length;
  const total = inventory.counts?.total ?? inventory.items.length;
  const unexpectedCount =
    inventory.counts?.unexpected ?? inventory.unexpectedScans.length;
  const term = search.trim().toLocaleLowerCase('ru');
  const rows = inventory.items.filter(
    item =>
      (filter === 'found'
        ? item.result === 'Found'
        : item.result !== 'Found') &&
      [
        item.assetId,
        item.inventoryNumber,
        item.name,
        item.locationName,
        item.departmentName,
      ].some(value => value?.toLocaleLowerCase('ru').includes(term)),
  );
  const unexpected = inventory.unexpectedScans.filter(scan =>
    [
      scan.rawValue,
      scan.recognizedInventoryNumber,
      scan.recognizedAssetName,
    ].some(value => value?.toLocaleLowerCase('ru').includes(term)),
  );
  const count =
    filter === 'unexpected'
      ? inventory.unexpectedTotalCount ?? unexpected.length
      : inventory.itemsTotalCount ?? rows.length;
  const pages = Math.max(1, Math.ceil(count / PAGE_SIZE));
  const currentPage = inventory.page ?? Math.min(page, pages);
  const visibleRows = inventory.page
    ? rows
    : rows.slice((currentPage - 1) * PAGE_SIZE, currentPage * PAGE_SIZE);
  const visibleScans = inventory.page
    ? unexpected
    : unexpected.slice((currentPage - 1) * PAGE_SIZE, currentPage * PAGE_SIZE);
  const choose = (value: Filter) => {
    setFilter(value);
    setPage(1);
  };
  const pager = pages > 1 ? (
    <View style={ui.row}>
      <Action title="Назад" secondary disabled={currentPage === 1 || query.isFetching} onPress={() => setPage(currentPage - 1)} />
      <Text style={ui.muted}>{currentPage} / {pages}</Text>
      <Action title="Далее" secondary disabled={currentPage === pages || query.isFetching} onPress={() => setPage(currentPage + 1)} />
    </View>
  ) : null;
  const finish = async () => {
    if (finishPending.current || !finishing) return;
    finishPending.current = true;
    setBusy(true);
    setError('');
    try {
      await mobileApi.finishScanning(id, finishing.round);
      await queryClient.invalidateQueries({
        queryKey: ['mobile-inventory', id],
      });
      await queryClient.invalidateQueries({ queryKey: ['mobile-inventories'] });
      setFinishing(null);
    } catch (failure) {
      setError(
        failure instanceof Error
          ? failure.message
          : 'Не удалось завершить обход.',
      );
    } finally {
      finishPending.current = false;
      setBusy(false);
    }
  };
  const active = inventory.status === 'Active';
  const hint = active
    ? 'Найдите устройства из списка и сканируйте их QR. После сохранения они исчезнут из списка.'
    : inventory.status === 'Completed'
      ? 'Итог подтверждён. Найденные устройства сохранены отдельно; ненайденные — в основном списке.'
    : inventory.status === 'AwaitingConfirmation'
    ? 'Обход завершён. Проверьте итог и подтвердите его паролем.'
    : inventory.status === 'Draft' || inventory.status === 'NeedsAttention'
    ? 'Ожидает запуска администратором. Состав появится после запуска.'
    : inventory.status === 'Cancelled'
    ? 'Проверка отменена. Результаты доступны только для просмотра.'
    : 'Результаты отклонены. Повторный запуск выполняет администратор.';
  return (
    <View style={ui.screen}>
      <ScrollView
        contentContainerStyle={ui.content}
        keyboardShouldPersistTaps="handled"
      >
        <View style={ui.row}>
          <Text style={[ui.title, ui.flex]}>{inventory.number}</Text>
          <Action
            title={query.isRefetching ? 'Обновление…' : 'Обновить'}
            secondary
            disabled={query.isRefetching || busy}
            onPress={() => query.refetch()}
          />
        </View>
        <View style={ui.card}>
          <Text style={ui.heading}>
            {inventoryStatusLabel(inventory.status)}
          </Text>
          <Text style={ui.muted}>{hint}</Text>
          <View style={ui.row}>
            <Text style={[ui.title, ui.flex]}>
              {found} / {total}
            </Text>
            <Text style={ui.muted}>ОС найдено</Text>
          </View>
          <Progress found={found} total={total} />
          <View style={ui.row}>
            <Text style={ui.text}>Осталось: {pending}</Text>
            <Text style={ui.text}>Не найдено: {missing}</Text>
          </View>
          <Text style={ui.muted}>
            Проблемы состояния: {damaged} · Посторонние QR: {unexpectedCount}
          </Text>
        </View>
        {inventory.status === 'Completed' && member ? <InventoryReports id={id} number={inventory.number} /> : null}
        <Text style={ui.heading}>
          {filter === 'found'
            ? 'Найденные устройства'
            : filter === 'unexpected'
            ? 'Посторонние QR'
            : active
            ? 'Нужно найти'
            : 'Не найденные устройства'}
        </Text>
        <View style={ui.row}>
          <Chip
            title={`${active ? 'Нужно найти' : 'Не найдено'} · ${
              pending + missing
            }`}
            selected={filter === 'remaining'}
            onPress={() => choose('remaining')}
          />
          <Chip
            title={`Найдено · ${found}`}
            selected={filter === 'found'}
            onPress={() => choose('found')}
          />
          <Chip
            title={`Другие QR · ${unexpectedCount}`}
            selected={filter === 'unexpected'}
            onPress={() => choose('unexpected')}
          />
        </View>
        <TextInput
          accessibilityLabel="Поиск ОС"
          placeholder="Номер, название или помещение"
          value={search}
          onChangeText={value => {
            setSearch(value);
            setPage(1);
          }}
          style={ui.input}
        />
        {pager}
        {query.isFetching ? <Text style={ui.muted}>Обновляю список…</Text> : null}
        {filter === 'unexpected'
          ? visibleScans.map(scan => (
              <View key={scan.id} style={ui.card}>
                <Text style={ui.label}>
                  {unexpectedScanTypeLabel(scan.type)}
                </Text>
                <Text style={ui.text}>
                  {[scan.recognizedInventoryNumber, scan.recognizedAssetName]
                    .filter(Boolean)
                    .join(' — ') || 'Код отсутствует в реестре'}
                </Text>
                <Text selectable style={ui.muted}>
                  {scan.rawValue}
                </Text>
              </View>
            ))
          : visibleRows.map(item => (
              <View key={item.id} style={ui.card}>
                <Text style={ui.label}>{item.inventoryNumber}</Text>
                <Text style={ui.heading}>{item.name}</Text>
                <Text style={ui.muted}>
                  {[item.departmentName, item.locationName]
                    .filter(Boolean)
                    .join(' · ')}
                </Text>
                <Text style={ui.muted}>
                  МОЛ: {item.materiallyResponsibleFullName}
                </Text>
                <Text style={ui.label}>
                  {item.result === 'Found'
                    ? 'Найдено'
                    : item.result === 'NotFound'
                    ? 'Не найдено'
                    : 'Ожидает сканирования'}
                  {item.result === 'Found'
                    ? ` · ${
                        item.condition === 'Damaged'
                          ? 'Повреждено'
                          : item.condition === 'Unusable'
                          ? 'Непригодно'
                          : 'Исправно'
                      }`
                    : ''}
                </Text>
                {item.comment ? (
                  <Text style={ui.text}>{item.comment}</Text>
                ) : null}
              </View>
            ))}
        {count === 0 ? (
          <View style={ui.card}>
            <Text style={ui.heading}>
              {term
                ? 'Ничего не найдено'
                : filter === 'found'
                ? 'Пока нет найденных ОС'
                : filter === 'unexpected'
                ? 'Посторонних QR нет'
                : total
                ? 'Все устройства найдены'
                : 'Состав ещё не сформирован'}
            </Text>
            <Text style={ui.muted}>
              {term
                ? 'Измените запрос или выберите другой список.'
                : 'Найденные устройства сохраняются в результатах, но не мешают обходу.'}
            </Text>
          </View>
        ) : (
          <Text style={ui.muted}>
            Показано {(currentPage - 1) * PAGE_SIZE + 1}–
            {Math.min(currentPage * PAGE_SIZE, count)} из {count}
          </Text>
        )}
        <View style={ui.card}>
          <Text style={ui.label}>
            Основание · {inventoryReasonLabel(inventory.reason)}
          </Text>
          {inventory.reasonDetails ? (
            <Text style={ui.muted}>{inventory.reasonDetails}</Text>
          ) : null}
          {assignee ? (
            <View>
              <Text style={ui.text}>{assignee.employeeName ?? assignee.login}</Text>
              <Text style={ui.muted}>
                Исполнитель ·{' '}
                {confirmationStatusLabel(assignee.confirmationStatus)}
              </Text>
              {assignee.rejectionReason ? (
                <Text style={ui.error}>{assignee.rejectionReason}</Text>
              ) : null}
            </View>
          ) : null}
        </View>
        {query.isError ? (
          <Text accessibilityRole="alert" style={ui.error}>
            Не удалось обновить данные. Показана последняя загруженная версия.
          </Text>
        ) : null}
      </ScrollView>
      {active && member ? (
        <View style={[ui.card, ui.dock]}>
          {finishing ? (
            <>
              <Text style={ui.heading}>Завершить обход?</Text>
              <Text style={ui.text}>
                {pending} ОС получат результат «Не найдено». Затем потребуется
                подтвердить итог паролем.
              </Text>
              {error ? (
                <Text accessibilityRole="alert" style={ui.error}>
                  {error}
                </Text>
              ) : null}
              <Action
                title={busy ? 'Завершение…' : 'Завершить и проверить итоги'}
                disabled={busy}
                onPress={finish}
              />
              <Action
                title="Продолжить сканирование"
                secondary
                disabled={busy}
                onPress={() => setFinishing(null)}
              />
            </>
          ) : (
            <>
              <Action
                title="Сканировать QR"
                onPress={() => navigation.navigate('Scan', { id, round: inventory.round })}
              />
              <Action
                title="Завершить обход"
                secondary
                onPress={() => {
                  setError('');
                  setFinishing({ round: inventory.round });
                }}
              />
            </>
          )}
        </View>
      ) : member && canConfirm(inventory.status, member.confirmationStatus) ? (
        <View style={[ui.card, ui.dock]}>
          <Action
            title="Проверить и подтвердить итог"
            onPress={() => navigation.navigate('Confirmation', { id, round: inventory.round })}
          />
        </View>
      ) : null}
    </View>
  );
}
