import { assetConditions, canConfirm, canFinishScanning, canScan, commissionRoleLabel, confirmationStatusLabel, inventoryReasonLabel, isUuid, requiresComment } from '../src/utils/inventory';

describe('inventory UI rules', () => {
  test('requires a comment only for damaged and unusable assets', () => {
    expect(requiresComment('Serviceable')).toBe(false);
    expect(requiresComment('Damaged')).toBe(true);
    expect(requiresComment('Unusable')).toBe(true);
  });

  test('allows scanning only for an active inventory', () => {
    expect(canScan('Active')).toBe(true);
    expect(canScan('AwaitingConfirmation')).toBe(false);
  });

  test('allows the assigned employee to finish an active inventory', () => {
    expect(canFinishScanning('Active', 'Chairperson')).toBe(true);
    expect(canFinishScanning('Active', 'Member')).toBe(true);
    expect(canFinishScanning('Active')).toBe(false);
    expect(canFinishScanning('Completed', 'Chairperson')).toBe(false);
  });

  test('does not offer confirmation after it was submitted', () => {
    expect(canConfirm('AwaitingConfirmation', 'Pending')).toBe(true);
    expect(canConfirm('AwaitingConfirmation', 'Confirmed')).toBe(false);
  });

  test('accepts UUIDv7 entered manually', () => {
    expect(isUuid('01991a2b-3c4d-7e5f-8a9b-0123456789ab')).toBe(true);
    expect(isUuid('not-a-uuid')).toBe(false);
  });

  test('shows Russian labels for user-facing inventory values', () => {
    expect(assetConditions.map((item) => item.label)).toEqual(['Исправно', 'Повреждено', 'Непригодно']);
    expect(inventoryReasonLabel('AnnualReporting')).toBe('Годовая отчётность');
    expect(commissionRoleLabel('Chairperson')).toBe('Инвентаризатор');
    expect(confirmationStatusLabel('Pending')).toBe('Ожидает решения');
  });
});
