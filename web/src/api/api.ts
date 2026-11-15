import { request, requestWithResponse } from './httpClient';
import type * as T from './types';
import type { components } from './generated/schema';

type Schema = components['schemas'];

const qs = (values: Record<string, unknown>) => { const p = new URLSearchParams(); Object.entries(values).forEach(([k,v]) => { if (v !== undefined && v !== null && v !== '') p.set(k, String(v)); }); return p.toString(); };
const json = (body: unknown, method = 'POST'): RequestInit => ({ method, body: JSON.stringify(body) });

export const api = {
  dashboard: () => request<T.Dashboard>('/api/v1/dashboard'),
  assets: (params: Record<string, unknown>) => request<T.PagedResponse<T.Asset>>(`/api/v1/assets?${qs(params)}`),
  asset: (id: string) => requestWithResponse<T.Asset>(`/api/v1/assets/${id}`),
  assetHistory: (id: string) => request<T.AssetHistory[]>(`/api/v1/assets/${id}/history`),
  createAsset: (body: T.AssetInput) => request<T.Asset>('/api/v1/assets', json(body)),
  updateAsset: (id: string, body: T.AssetInput, etag: string) => request<T.Asset>(`/api/v1/assets/${id}`, { ...json(body, 'PUT'), headers: { 'If-Match': etag } }),
  archiveAsset: (id: string) => request<void>(`/api/v1/assets/${id}`, { method: 'DELETE' }),
  departments: (includeArchived = false) => request<T.Department[]>(`/api/v1/departments?includeArchived=${includeArchived}`),
  locations: (departmentId?: string, includeArchived = false) => request<T.Location[]>(`/api/v1/locations?${qs({ departmentId, includeArchived })}`),
  employees: (includeArchived = false) => request<T.Employee[]>(`/api/v1/employees?includeArchived=${includeArchived}`),
  users: () => request<T.User[]>('/api/v1/users'),
  organization: () => request<T.Organization>('/api/v1/organization'),
  saveOrganization: (body: T.Organization) => request<T.Organization>('/api/v1/organization', json(body, 'PUT')),
  createDepartment: (body: Omit<T.Department,'id'|'isArchived'>) => request<T.Department>('/api/v1/departments', json(body)),
  updateDepartment: (id:string, body: Omit<T.Department,'id'|'isArchived'>) => request<T.Department>(`/api/v1/departments/${id}`, json(body,'PUT')),
  archiveDepartment: (id:string) => request<void>(`/api/v1/departments/${id}`, {method:'DELETE'}),
  createLocation: (body: {code:string;name:string;departmentId:string}) => request<T.Location>('/api/v1/locations', json(body)),
  updateLocation: (id:string, body: {code:string;name:string;departmentId:string}) => request<T.Location>(`/api/v1/locations/${id}`, json(body,'PUT')),
  archiveLocation: (id:string) => request<void>(`/api/v1/locations/${id}`, {method:'DELETE'}),
  createEmployee: (body: {personnelNumber:string;fullName:string;departmentId:string;isMateriallyResponsible:boolean;isEmployed:boolean}) => request<T.Employee>('/api/v1/employees', json(body)),
  updateEmployee: (id:string, body: {personnelNumber:string;fullName:string;departmentId:string;isMateriallyResponsible:boolean;isEmployed:boolean}) => request<T.Employee>(`/api/v1/employees/${id}`, json(body,'PUT')),
  archiveEmployee: (id:string) => request<void>(`/api/v1/employees/${id}`, {method:'DELETE'}),
  createUser: (body: {login:string;password:string;role:string;employeeId?:string|null}) => request<T.User>('/api/v1/users', json(body)),
  setUserBlocked: (id:string,isBlocked:boolean) => request<T.User>(`/api/v1/users/${id}/blocked`, json({isBlocked},'PATCH')),
  inventories: (params: Record<string, unknown>) => request<T.PagedResponse<T.InventorySummary>>(`/api/v1/inventories?${qs(params)}`),
  inventory: (id: string, params?: Record<string, unknown>) => request<T.InventoryDetail>(`/api/v1/inventories/${id}${params ? `?${qs(params)}` : ''}`),
  paged: <TItem>(endpoint: string, params: Record<string, unknown>) => request<T.PagedResponse<TItem>>(`/api/v1/${endpoint}/paged?${qs(params)}`),
  createInventory: (body: Schema['CreateInventoryRequest']) => request<T.InventoryDetail>('/api/v1/inventories', json(body)),
  startInventory: (id: string) => request<T.InventoryDetail>(`/api/v1/inventories/${id}/start`, { method: 'POST' }),
  cancelInventory: (id: string) => request<T.InventoryDetail>(`/api/v1/inventories/${id}/cancel`, { method: 'POST' }),
  cloneInventory: (id: string, plannedAt: string) => request<T.InventoryDetail>(`/api/v1/inventories/${id}/clone`, json({ plannedAt })),
  replaceAssignee: (id: string, body: Schema['ReplaceAssigneeRequest']) => request<T.InventoryDetail>(`/api/v1/inventories/${id}/assignee`, json(body,'PUT')),
  schedules: () => request<T.Schedule[]>('/api/v1/inventory-schedules'),
  createSchedule: (body: Schema['UpsertScheduleRequest']) => request<T.Schedule>('/api/v1/inventory-schedules',json(body)),
  updateSchedule: (id: string, body: Schema['UpsertScheduleRequest']) => request<T.Schedule>(`/api/v1/inventory-schedules/${id}`,json(body,'PUT')),
  deleteSchedule: (id:string) => request<void>(`/api/v1/inventory-schedules/${id}`,{method:'DELETE'}),
  events: () => request<T.IntegrationEvent[]>('/api/v1/inventory-events'),
  createMolEvent: (body: Schema['MolChangeEventRequest']) => request<T.IntegrationEvent>('/api/v1/inventory-events/mol-change',json(body)),
  retryEvent: (id: string) => request<T.IntegrationEvent>(`/api/v1/inventory-events/${id}/retry`, { method: 'POST' }),
  audit: (page: number, pageSize: number, filters: Record<string, unknown> = {}) => request<T.PagedResponse<T.AuditEntry>>(`/api/v1/audit?${qs({ ...filters, page, pageSize })}`),
};
