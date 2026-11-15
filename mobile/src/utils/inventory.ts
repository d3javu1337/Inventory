export const requiresComment = (condition: string) =>
  condition === 'Damaged' || condition === 'Unusable';

export const assetConditions = [
  { value: 'Serviceable', label: 'Исправно' },
  { value: 'Damaged', label: 'Повреждено' },
  { value: 'Unusable', label: 'Непригодно' },
] as const;

const inventoryReasonLabels: Record<string, string> = {
  AnnualReporting: 'Годовая отчётность',
  MolChange: 'Смена материально ответственного лица',
  MolDismissal: 'Увольнение материально ответственного лица',
  AssetTransfer: 'Передача имущества',
  LossOrDamage: 'Утрата или повреждение',
  Emergency: 'Чрезвычайная ситуация',
  Reorganization: 'Реорганизация',
  Liquidation: 'Ликвидация',
  Other: 'Другое',
};

const commissionRoleLabels: Record<string, string> = {
  Chairperson: 'Инвентаризатор',
  Member: 'Инвентаризатор',
};

const confirmationStatusLabels: Record<string, string> = {
  Pending: 'Ожидает решения',
  Confirmed: 'Подтверждено',
  Rejected: 'Отклонено',
};

const unexpectedScanTypeLabels: Record<string, string> = {
  UnknownQr: 'Неизвестный QR-код',
  OutOfScope: 'Не входит в проверку',
};

export const inventoryReasonLabel = (value: string) =>
  inventoryReasonLabels[value] ?? value;
export const commissionRoleLabel = (value: string) =>
  commissionRoleLabels[value] ?? value;
export const confirmationStatusLabel = (value: string) =>
  confirmationStatusLabels[value] ?? value;
export const unexpectedScanTypeLabel = (value: string) =>
  unexpectedScanTypeLabels[value] ?? value;
const statusLabels: Record<string, string> = {
  Draft: 'Ожидает запуска',
  NeedsAttention: 'Требует внимания',
  Active: 'Идёт обход',
  AwaitingConfirmation: 'Нужно подтвердить итог',
  Completed: 'Завершена',
  ReviewRequired: 'Требует повторной проверки',
  Cancelled: 'Отменена',
};
export const inventoryStatusLabel = (value: string) =>
  statusLabels[value] ?? value;

export const isUuid = (value: string) =>
  /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(
    value.trim(),
  );

export const canScan = (status: string) => status === 'Active';

export const canFinishScanning = (status: string, role?: string) =>
  status === 'Active' && !!role;

export const canConfirm = (status: string, confirmationStatus?: string) =>
  status === 'AwaitingConfirmation' && confirmationStatus !== 'Confirmed';
