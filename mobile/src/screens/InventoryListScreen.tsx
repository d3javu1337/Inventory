import React, { useEffect, useRef, useState } from 'react';
import {
  ActivityIndicator,
  AppState,
  FlatList,
  Pressable,
  RefreshControl,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  View,
} from 'react-native';
import { useIsFocused } from '@react-navigation/native';
import { StackScreenProps } from '@react-navigation/stack';
import { useInfiniteQuery } from '@tanstack/react-query';
import { RootStackParamList } from '../navigation/types';
import { mobileApi } from '../api/mobileApi';
import { session } from '../auth/session';
import { InventorySummary } from '../types/api';
import { inventoryReasonLabel, inventoryStatusLabel } from '../utils/inventory';
import { Chip, Progress } from '../ui/kit';

type Props = StackScreenProps<RootStackParamList, 'Inventories'>;

export function InventoryListScreen({ navigation }: Props) {
  const loggingOut = useRef(false);
  const focused = useIsFocused();
  const [appState, setAppState] = useState(AppState.currentState);
  const [status, setStatus] = useState<string | undefined>('Active');
  const [search, setSearch] = useState('');
  const [querySearch, setQuerySearch] = useState('');
  useEffect(() => {
    const timer = setTimeout(() => setQuerySearch(search.trim()), 300);
    return () => clearTimeout(timer);
  }, [search]);
  useEffect(() => {
    const subscription = AppState.addEventListener('change', setAppState);
    return () => subscription.remove();
  }, []);
  const watchActive = focused && appState !== 'background' && appState !== 'inactive' && status === 'Active';
  const query = useInfiniteQuery({
    queryKey: ['mobile-inventories', status ?? 'all', querySearch],
    initialPageParam: 1,
    queryFn: ({ pageParam }) => mobileApi.inventories(pageParam, status, querySearch),
    refetchInterval: current => watchActive && (current.state.data?.pages.length ?? 0) <= 1 ? 15_000 : false,
    getNextPageParam: lastPage =>
      lastPage.page * lastPage.pageSize < lastPage.totalCount
        ? lastPage.page + 1
        : undefined,
  });
  const inventories = query.data?.pages.flatMap(page => page.items) ?? [];
  const logout = async () => {
    if (loggingOut.current) return;
    loggingOut.current = true;
    const refreshToken = await session.signOut().catch(() => null);
    if (refreshToken) await mobileApi.logout(refreshToken).catch(() => undefined);
  };
  const errorStatus = (query.error as (Error & { status?: number }) | null)?.status;
  const showError = query.isError && (inventories.length === 0 || errorStatus === 401 || errorStatus === 403);
  return (
    <View style={styles.container}>
      <View style={styles.header}>
        <Text style={styles.heading}>Назначенные проверки</Text>
        <View style={styles.headerActions}>
          <Pressable accessibilityRole="button" onPress={() => navigation.navigate('ChangePassword')}>
            <Text style={styles.link}>Пароль</Text>
          </Pressable>
          <Pressable
            accessibilityRole="button"
            accessibilityState={{ disabled: query.isRefetching }}
            disabled={query.isRefetching}
            onPress={() => query.refetch()}
          >
            <Text
              style={[styles.link, query.isRefetching && styles.linkDisabled]}
            >
              {query.isRefetching ? 'Обновление…' : 'Обновить'}
            </Text>
          </Pressable>
          <Pressable accessibilityRole="button" onPress={logout}>
            <Text style={styles.link}>Выйти</Text>
          </Pressable>
        </View>
      </View>
      <View style={styles.searchRow}>
        <TextInput style={styles.search} placeholder="Номер, UUID, МОЛ или исполнитель" accessibilityLabel="Поиск инвентаризаций" value={search} onChangeText={setSearch} maxLength={200} autoCapitalize="none" autoCorrect={false} returnKeyType="search" onSubmitEditing={() => setQuerySearch(search.trim())} />
        {search ? <Pressable accessibilityRole="button" onPress={() => { setSearch(''); setQuerySearch(''); }}><Text style={styles.link}>Очистить</Text></Pressable> : null}
      </View>
      <ScrollView
        horizontal
        style={styles.filters}
        contentContainerStyle={styles.filterContent}
        showsHorizontalScrollIndicator={false}
      >
        {[
          { title: 'Активные', value: 'Active' },
          { title: 'Подтверждение', value: 'AwaitingConfirmation' },
          { title: 'Черновики', value: 'Draft' },
          { title: 'Требуют внимания', value: 'NeedsAttention' },
          { title: 'Доработка', value: 'ReviewRequired' },
          { title: 'Завершённые', value: 'Completed' },
          { title: 'Отменённые', value: 'Cancelled' },
          { title: 'Все', value: undefined },
        ].map(filter => (
          <Chip
            key={filter.title}
            title={filter.title}
            selected={status === filter.value}
            onPress={() => setStatus(filter.value)}
          />
        ))}
      </ScrollView>
      <FlatList
        data={showError ? [] : inventories}
        keyExtractor={item => item.id}
        refreshControl={
          <RefreshControl
            refreshing={query.isRefetching}
            onRefresh={() => query.refetch()}
          />
        }
        onEndReached={() =>
          !query.isError &&
          query.hasNextPage &&
          !query.isFetchingNextPage &&
          query.fetchNextPage()
        }
        contentContainerStyle={
          showError || inventories.length === 0 ? styles.empty : styles.list
        }
        ListEmptyComponent={
          query.isLoading ? <View style={styles.loading}><ActivityIndicator size="large" /><Text>Загрузка проверок…</Text></View> : showError ? <View style={styles.center}>
            <Text style={styles.errorText}>{errorStatus === 403 ? 'У этой учетной записи нет доступа к мобильному приложению. Войдите как инвентаризатор.' : 'Не удалось загрузить проверки.'}</Text>
            <Pressable accessibilityRole="button" onPress={() => query.refetch()}><Text style={styles.link}>Повторить</Text></Pressable>
          </View> : <Text>
            {querySearch ? 'По вашему запросу проверок не найдено. Измените поиск или категорию.' : status
              ? 'В этой категории проверок нет. Выберите другую категорию или «Все».'
              : 'Назначенных проверок нет.'}
          </Text>
        }
        ListFooterComponent={
          query.isFetchingNextPage ? <ActivityIndicator /> : null
        }
        renderItem={({ item }) => (
          <InventoryCard
            item={item}
            onPress={() => navigation.navigate('Inventory', { id: item.id })}
          />
        )}
      />
    </View>
  );
}
function InventoryCard({
  item,
  onPress,
}: {
  item: InventorySummary;
  onPress: () => void;
}) {
  return (
    <Pressable accessibilityRole="button" style={styles.card} onPress={onPress}>
      <Text style={styles.status}>{inventoryStatusLabel(item.status)}</Text>
      <Text style={styles.number}>{item.number}</Text>
      <Text>{inventoryReasonLabel(item.reason)}</Text>
      {item.targetEmployeeName ? <Text>МОЛ: {item.targetEmployeeName}</Text> : null}
      {item.assigneeName ? <Text>Исполнитель: {item.assigneeName}</Text> : null}
      <Text style={styles.progress}>
        Найдено: {item.found} / {item.total} ·{' '}
        {item.status === 'Active'
          ? `Нужно найти: ${item.pending}`
          : `Не найдено: ${item.notFound}`}
      </Text>
      <Progress found={item.found} total={item.total} />
      <Text style={styles.link}>Открыть проверку →</Text>
    </Pressable>
  );
}
const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: '#f3f5f8' },
  searchRow: { paddingHorizontal: 14, paddingBottom: 12, flexDirection: 'row', alignItems: 'center', gap: 12 },
  search: { flex: 1, borderWidth: 1, borderColor: '#dce3ec', backgroundColor: '#fff', borderRadius: 8, padding: 10 },
  loading: {
    flex: 1,
    alignItems: 'center',
    justifyContent: 'center',
    gap: 12,
    backgroundColor: '#fff',
  },
  header: {
    padding: 14,
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'center',
    gap: 12,
  },
  headerActions: { flexDirection: 'row', alignItems: 'center', gap: 18 },
  filters: { flexGrow: 0, flexShrink: 0 },
  filterContent: { paddingHorizontal: 14, paddingBottom: 12, gap: 8 },
  heading: { flex: 1, fontSize: 20, fontWeight: '700' },
  list: { padding: 12, gap: 10 },
  empty: { flex: 1, alignItems: 'center', justifyContent: 'center' },
  center: {
    flex: 1,
    alignItems: 'center',
    justifyContent: 'center',
    gap: 18,
    padding: 24,
  },
  errorText: {
    color: '#6b2020',
    fontSize: 16,
    lineHeight: 24,
    textAlign: 'center',
  },
  errorActions: { flexDirection: 'row', gap: 28 },
  card: {
    borderWidth: 1,
    borderColor: '#dce3ec',
    backgroundColor: '#fff',
    borderRadius: 16,
    padding: 18,
    gap: 10,
  },
  number: { fontSize: 20, fontWeight: '700', color: '#16263b' },
  status: { color: '#315d9b' },
  progress: { marginTop: 6 },
  link: { color: '#195ec8', fontWeight: '600' },
  linkDisabled: { opacity: 0.5 },
});
