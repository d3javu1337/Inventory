import React, { useEffect, useState } from 'react';
import { ActivityIndicator, Platform, View } from 'react-native';
import { NavigationContainer } from '@react-navigation/native';
import { createStackNavigator } from '@react-navigation/stack';
import { RootStackParamList } from './types';
import { session } from '../auth/session';
import { refreshSession } from '../api/client';
import { mobileApi } from '../api/mobileApi';
import { LoginScreen } from '../screens/LoginScreen';
import { ChangePasswordScreen } from '../screens/ChangePasswordScreen';
import { InventoryListScreen } from '../screens/InventoryListScreen';
import { InventoryDetailsScreen } from '../screens/InventoryDetailsScreen';
import { ScanScreen } from '../screens/ScanScreen';
import { AssetResultScreen } from '../screens/AssetResultScreen';
import { ConfirmationScreen } from '../screens/ConfirmationScreen';

const Stack = createStackNavigator<RootStackParamList>();

export function RootNavigator() {
  const [ready, setReady] = useState(false);
  const [signedIn, setSignedIn] = useState(false);

  useEffect(() => {
    const unsubscribe = session.subscribe(() => setSignedIn(false));
    let active = true;
    const restore = async () => {
      try {
        let restored = await refreshSession();
        if (restored && session.getUser()?.role !== 'InventoryEmployee') {
          const token = await session.signOut();
          if (token) await mobileApi.logout(token).catch(() => undefined);
          restored = false;
        }
        if (active) setSignedIn(restored);
      } catch {
        // Temporary network/Keystore failures must not trap the user on a spinner or destroy a valid refresh token.
      } finally {
        if (active) setReady(true);
      }
    };
    restore();
    return () => { active = false; unsubscribe(); };
  }, []);

  if (!ready) return <View style={styles.loading}><ActivityIndicator size="large" /></View>;

  return <NavigationContainer>
    <Stack.Navigator
      screenOptions={{
        animation: Platform.OS === 'web' ? 'none' : 'default',
        cardStyle: { backgroundColor: '#f3f5f8' },
        headerTintColor: '#16263b',
        headerStyle: { backgroundColor: '#fff' },
      }}
    >
      {!signedIn ? <Stack.Screen name="Login" options={{ title: 'Вход' }}>{(props) => <LoginScreen {...props} onSignedIn={() => setSignedIn(true)} />}</Stack.Screen> : null}
      {signedIn ? <>
        <Stack.Screen name="Inventories" component={InventoryListScreen} options={{ title: 'Инвентаризации' }} />
        <Stack.Screen name="ChangePassword" component={ChangePasswordScreen} options={{ title: 'Смена пароля' }} />
        <Stack.Screen name="Inventory" component={InventoryDetailsScreen} options={{ title: 'Проверка' }} />
        <Stack.Screen name="Scan" component={ScanScreen} options={{ title: 'Сканирование QR' }} />
        <Stack.Screen name="AssetResult" component={AssetResultScreen} options={{ title: 'Состояние ОС' }} />
        <Stack.Screen name="Confirmation" component={ConfirmationScreen} options={{ title: 'Подтверждение' }} />
      </> : null}
    </Stack.Navigator>
  </NavigationContainer>;
}

const styles = {
  loading: { flex: 1, alignItems: 'center' as const, justifyContent: 'center' as const },
};
