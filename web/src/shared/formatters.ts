const timezone = 'Asia/Yekaterinburg';
export const formatDate = (value?: string | null) => value ? new Intl.DateTimeFormat('ru-RU', { timeZone: timezone }).format(new Date(value)) : '—';
export const formatDateTime = (value?: string | null) => value ? new Intl.DateTimeFormat('ru-RU', { dateStyle: 'short', timeStyle: 'short', timeZone: timezone }).format(new Date(value)) : '—';
const currency = new Intl.NumberFormat('ru-RU', { style: 'currency', currency: 'RUB', minimumFractionDigits: 2 });
export function formatMoney(value?: number | string | null) {
  if (typeof value !== 'string') return currency.format(value ?? 0);
  const match = /^(\d+)\.(\d{2})$/.exec(value);
  if (!match) return '—';
  return currency.formatToParts(BigInt(match[1]))
    .map(part => part.type === 'fraction' ? match[2] : part.value).join('');
}
export const percent = (value: number, total: number) => total ? Math.round(value / total * 100) : 0;
