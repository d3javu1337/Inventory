export const queryKeys = {
  dashboard: ['dashboard'] as const,
  assets: (filters: object) => ['assets', filters] as const,
  asset: (id: string) => ['asset', id] as const,
  assetHistory: (id: string) => ['assetHistory', id] as const,
  departments: ['departments'] as const, locations: (departmentId?: string) => ['locations', departmentId] as const,
  employees: ['employees'] as const, users: ['users'] as const, organization: ['organization'] as const,
  inventories: (filters: object) => ['inventories', filters] as const, inventory: (id: string) => ['inventory', id] as const,
  schedules: ['schedules'] as const, events: ['integrationEvents'] as const, audit: (page: number, size: number, filters: object = {}) => ['audit', page, size, filters] as const,
};
