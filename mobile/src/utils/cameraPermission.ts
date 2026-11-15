import { PermissionsAndroid, Platform } from 'react-native';

export async function checkCameraPermission(): Promise<boolean> {
  return Platform.OS !== 'android' || await PermissionsAndroid.check(PermissionsAndroid.PERMISSIONS.CAMERA);
}

export async function requestCameraPermission(): Promise<boolean> {
  return Platform.OS !== 'android' || await PermissionsAndroid.request(PermissionsAndroid.PERMISSIONS.CAMERA) === PermissionsAndroid.RESULTS.GRANTED;
}
