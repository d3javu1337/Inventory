export type AuthUser = {
  id: string;
  login: string;
  role: string;
  employeeId: string | null;
};

export type TokenResponse = {
  accessToken: string;
  refreshToken: string;
  accessTokenExpiresAt: string;
  refreshTokenExpiresAt: string;
  user: AuthUser;
};

export type InventorySummary = {
  assigneeName?: string | null;
  targetEmployeeName?: string | null;
  id: string;
  number: string;
  type: string;
  reason: string;
  status: string;
  plannedAt: string;
  total: number;
  found: number;
  notFound: number;
  pending: number;
};

export type InventoryItem = {
  id: string;
  assetId: string;
  inventoryNumber: string;
  name: string;
  accountingAccount: string;
  initialCost: number;
  departmentName: string;
  locationName: string;
  materiallyResponsibleFullName: string;
  result: string;
  condition: string | null;
  comment: string | null;
  scannedAt: string | null;
};

export type InventoryAssignee = {
  id: string;
  userId: string;
  login: string;
  employeeName: string | null;
  role: string;
  confirmationStatus: string;
  confirmedAt: string | null;
  rejectionReason: string | null;
};

export type UnexpectedScan = {
  id: string;
  rawValue: string;
  type: string;
  recognizedAssetId: string | null;
  recognizedInventoryNumber: string | null;
  recognizedAssetName: string | null;
  scannedAt: string;
};

export type InventoryDetail = {
  counts?: { total: number; found: number; pending: number; notFound: number; damaged: number; discrepancies: number; unexpected: number };
  page?: number;
  pageSize?: number;
  itemsTotalCount?: number;
  unexpectedTotalCount?: number;
  id: string;
  number: string;
  type: string;
  reason: string;
  reasonDetails: string | null;
  status: string;
  targetEmployeeId: string | null;
  plannedAt: string;
  startedAt: string | null;
  round?: number;
  scanningFinishedAt: string | null;
  completedAt: string | null;
  items: InventoryItem[];
  assignee?: InventoryAssignee | null;
  commission: InventoryAssignee[];
  unexpectedScans: UnexpectedScan[];
};

export type ScanRequest = { qrValue: string; condition: string; comment?: string | null };
export type ScanResponse = { result: string; duplicate: boolean; asset: InventoryItem | null; message: string };
export type ProblemDetails = { title?: string; detail?: string; code?: string; traceId?: string; errors?: Record<string, string[]> };
export type PagedResponse<T> = { items: T[]; page: number; pageSize: number; totalCount: number };
