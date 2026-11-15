import React, { useRef, useState } from 'react';
import { Platform, Text, View } from 'react-native';
import { downloadInventoryReport, ReportFormat } from '../files/inventoryReport';
import { Action, ui } from '../ui/kit';

export function InventoryReports({ id, number }: { id: string; number: string }) {
  const [busy, setBusy] = useState<ReportFormat | null>(null);
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');
  const pending = useRef(false);
  const download = async (format: ReportFormat) => {
    if (pending.current) return;
    pending.current = true;
    setBusy(format); setMessage(''); setError('');
    try {
      const saved = await downloadInventoryReport(id, number, format);
      if (saved) setMessage(Platform.OS === 'web' ? 'Скачивание отчёта начато.' : 'Отчёт сохранён.');
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось сохранить отчёт. Повторите попытку.');
    } finally { pending.current = false; setBusy(null); }
  };
  return <View style={ui.card}>
    <Text style={ui.heading}>Отчёты ИНВ-1</Text>
    <Text style={ui.muted}>Сохранённые результаты подтверждённой проверки.</Text>
    {error ? <Text accessibilityRole="alert" style={ui.error}>{error}</Text> : null}
    {message ? <Text accessibilityRole="alert" style={ui.text}>{message}</Text> : null}
    <Action title={busy === 'pdf' ? 'Подготовка PDF…' : 'Сохранить PDF'} disabled={busy !== null} onPress={() => download('pdf')} />
    <Action title={busy === 'xlsx' ? 'Подготовка XLSX…' : 'Сохранить XLSX'} secondary disabled={busy !== null} onPress={() => download('xlsx')} />
  </View>;
}
