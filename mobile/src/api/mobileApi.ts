import { apiFetch } from './client';
import { InventoryDetail, InventorySummary, PagedResponse, ScanRequest, ScanResponse, TokenResponse } from '../types/api';

function roundParameter(round: number | undefined, separator: '?' | '&') {
  return round === undefined ? '' : `${separator}expectedRound=${round}`;
}

export const mobileApi = {
  changePassword: (currentPassword: string, newPassword: string, device: string) => apiFetch<TokenResponse>('/auth/mobile/change-password', { method: 'POST', body: JSON.stringify({ currentPassword, newPassword, device }) }),
  login: (login: string, password: string, device: string) => apiFetch<TokenResponse>('/auth/mobile/login', { method: 'POST', body: JSON.stringify({ login, password, device }) }),
  refresh: (refreshToken: string, device: string) => apiFetch<TokenResponse>('/auth/mobile/refresh', { method: 'POST', body: JSON.stringify({ refreshToken, device }) }),
  logout: (refreshToken: string) => apiFetch<void>('/auth/mobile/logout', { method: 'POST', body: JSON.stringify({ refreshToken }) }),
  inventories: (page = 1, status?: string, search = '') => apiFetch<PagedResponse<InventorySummary>>(`/mobile/inventories?page=${page}&pageSize=20${status ? `&status=${encodeURIComponent(status)}` : ''}${search.trim() ? `&search=${encodeURIComponent(search.trim())}` : ''}`),
  inventory: (id: string, page = 1, filter = 'all', search = '') => apiFetch<InventoryDetail>(`/mobile/inventories/${id}?page=${page}&pageSize=20&filter=${encodeURIComponent(filter)}&search=${encodeURIComponent(search)}`),
  scan: (id: string, request: ScanRequest, round?: number) => apiFetch<ScanResponse>(`/mobile/inventories/${id}/scan${roundParameter(round, '?')}`, { method: 'POST', body: JSON.stringify(request) }),
  finishScanning: (id: string, round?: number) => apiFetch<void>(`/mobile/inventories/${id}/finish-scanning?includeDetails=false${roundParameter(round, '&')}`, { method: 'POST' }),
  confirm: (id: string, password: string, round?: number) => apiFetch<void>(`/mobile/inventories/${id}/confirm?includeDetails=false${roundParameter(round, '&')}`, { method: 'POST', body: JSON.stringify({ password }) }, 60_000),
  reject: (id: string, password: string, reason: string, round?: number) => apiFetch<void>(`/mobile/inventories/${id}/reject?includeDetails=false${roundParameter(round, '&')}`, { method: 'POST', body: JSON.stringify({ password, reason }) }),
};
