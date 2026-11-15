import { Alert, Platform } from 'react-native';

// react-native-web does not implement Alert.alert. Preserve the same callbacks.
export const appAlert: typeof Alert.alert = (title, message, buttons, options) => {
  if (Platform.OS !== 'web') return Alert.alert(title, message, buttons, options);
  const text = [title, message].filter(Boolean).join('\n\n');
  const actions = buttons ?? [{ text: 'OK' }];
  const accept = actions.find(button => button.style !== 'cancel');
  const cancel = actions.find(button => button.style === 'cancel');
  if (actions.length > 1) {
    // eslint-disable-next-line no-alert -- web equivalent of the native confirmation
    if (window.confirm(text)) accept?.onPress?.();
    else cancel?.onPress?.();
  } else {
    // eslint-disable-next-line no-alert -- web equivalent of the native result alert
    window.alert(text);
    accept?.onPress?.();
  }
};
