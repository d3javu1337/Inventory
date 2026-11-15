import * as Keychain from 'react-native-keychain';

const SERVICE = 'asset-inventory-mobile';

export async function getRefreshCredentials() {
  const credentials = await Keychain.getGenericPassword({ service: SERVICE });
  return credentials
    ? { token: credentials.password, userId: credentials.username === 'refresh-token' ? null : credentials.username }
    : null;
}

export async function saveRefreshToken(token: string, userId: string) {
  const saved = await Keychain.setGenericPassword(userId, token, { service: SERVICE });
  if (!saved) throw new Error('Не удалось сохранить сессию на устройстве.');
}

export async function clearRefreshToken() {
  await Keychain.resetGenericPassword({ service: SERVICE });
}
