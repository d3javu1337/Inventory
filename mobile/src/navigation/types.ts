export type RootStackParamList = {
  Login: undefined;
  Inventories: undefined;
  ChangePassword: undefined;
  Inventory: { id: string };
  Scan: { id: string; round?: number };
  AssetResult: { id: string; qrValue: string; round?: number };
  Confirmation: { id: string; round?: number };
};
