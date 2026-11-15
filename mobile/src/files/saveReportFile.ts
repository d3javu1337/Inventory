import { NativeModules } from 'react-native';

export async function saveReportFile(blob: Blob, fileName: string, mimeType: string, stillCurrent: () => boolean): Promise<boolean> {
  if (!NativeModules.ReportFiles?.save) throw new Error('Для сохранения отчётов обновите приложение.');
  const base64 = await new Promise<string>((resolve, reject) => {
    const reader = new FileReader();
    reader.onerror = () => reject(new Error('Не удалось подготовить отчёт к сохранению.'));
    reader.onload = () => {
      const result = String(reader.result ?? '');
      const comma = result.indexOf(',');
      if (comma < 0) reject(new Error('Не удалось прочитать отчёт.'));
      else resolve(result.slice(comma + 1));
    };
    reader.readAsDataURL(blob);
  });
  if (!stillCurrent()) throw new Error('Сессия изменилась. Повторите запрос.');
  return NativeModules.ReportFiles.save(fileName, mimeType, base64);
}
