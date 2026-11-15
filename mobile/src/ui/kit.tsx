import React from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';
export const colors = {
  background: '#f3f5f8',
  ink: '#16263b',
  muted: '#64748b',
  line: '#dce3ec',
  primary: '#2459d3',
};
export const ui = StyleSheet.create({
  screen: { flex: 1, backgroundColor: colors.background },
  content: { padding: 18, gap: 16 },
  card: {
    backgroundColor: '#fff',
    borderRadius: 16,
    padding: 18,
    gap: 10,
    borderWidth: 1,
    borderColor: colors.line,
  },
  title: { fontSize: 26, fontWeight: '700', color: colors.ink },
  heading: { fontSize: 18, fontWeight: '700', color: colors.ink },
  text: { fontSize: 15, lineHeight: 22, color: colors.ink },
  muted: { fontSize: 13, lineHeight: 20, color: colors.muted },
  label: { fontSize: 13, fontWeight: '600', color: colors.ink },
  row: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 10,
    flexWrap: 'wrap',
  },
  input: {
    borderWidth: 1,
    borderColor: colors.line,
    backgroundColor: '#fff',
    borderRadius: 12,
    padding: 14,
    minHeight: 48,
    fontSize: 16,
    color: colors.ink,
  },
  error: { color: '#ae3831', fontSize: 14, lineHeight: 21 },
  flex: { flex: 1 },
  dock: { borderRadius: 0 },
  comment: { minHeight: 110, textAlignVertical: 'top' },
});
export function Action({
  title,
  onPress,
  disabled = false,
  secondary = false,
}: {
  title: string;
  onPress: () => void;
  disabled?: boolean;
  secondary?: boolean;
}) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityState={{ disabled }}
      disabled={disabled}
      onPress={onPress}
      style={[
        styles.button,
        secondary && styles.secondary,
        disabled && styles.disabled,
      ]}
    >
      <Text style={[styles.buttonText, secondary && styles.secondaryText]}>
        {title}
      </Text>
    </Pressable>
  );
}
export function Chip({
  title,
  selected,
  onPress,
}: {
  title: string;
  selected: boolean;
  onPress: () => void;
}) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityState={{ selected }}
      onPress={onPress}
      style={[styles.chip, selected && styles.selected]}
    >
      <Text style={[styles.chipText, selected && styles.selectedText]}>
        {title}
      </Text>
    </Pressable>
  );
}
export function Progress({ found, total }: { found: number; total: number }) {
  return (
    <View
      accessibilityLabel={`Найдено ${found} из ${total}`}
      style={styles.track}
    >
      <View
        style={[
          styles.fill,
          { width: `${total ? Math.min(100, (found / total) * 100) : 0}%` },
        ]}
      />
    </View>
  );
}
const styles = StyleSheet.create({
  disabled: { opacity: 0.45 },
  secondaryText: { color: colors.primary },
  selected: { backgroundColor: colors.primary },
  selectedText: { color: '#fff' },
  button: {
    minHeight: 48,
    borderRadius: 12,
    padding: 13,
    backgroundColor: colors.primary,
    alignItems: 'center',
    justifyContent: 'center',
  },
  buttonText: { color: '#fff', fontSize: 15, fontWeight: '700' },
  secondary: { backgroundColor: '#eaf0ff' },
  chip: {
    minHeight: 44,
    paddingHorizontal: 13,
    paddingVertical: 11,
    borderRadius: 12,
    backgroundColor: '#e8edf4',
  },
  chipText: { color: colors.muted, fontSize: 13, fontWeight: '600' },
  track: {
    height: 7,
    backgroundColor: '#e8edf4',
    borderRadius: 4,
    overflow: 'hidden',
  },
  fill: { height: 7, backgroundColor: '#167451' },
});
