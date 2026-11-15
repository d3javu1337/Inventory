import { z } from 'zod';

const requiredText = (label: string, max: number) =>
  z.string().trim().min(1, `Заполните поле «${label}»`).max(max, `Не более ${max} символов`);

const optionalText = (max: number) =>
  z.string().trim().max(max, `Не более ${max} символов`).nullable().optional();

const uuid = (label: string) => z.string().uuid(`Выберите ${label}`);

const validDate = (value: string) => {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(value)) return false;
  const date = new Date(`${value}T00:00:00`);
  return Number(value.slice(0, 4)) >= 1 && !Number.isNaN(date.getTime()) && date.getFullYear() === Number(value.slice(0, 4)) && date.getMonth() + 1 === Number(value.slice(5, 7)) && date.getDate() === Number(value.slice(8, 10));
};

export const dateSchema = z.string().refine(validDate, 'Введите корректную дату');
export const dateTimeSchema = z.string().refine(value => {
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/.test(value) || !validDate(value.slice(0, 10))) return false;
  return Number(value.slice(11, 13)) < 24 && Number(value.slice(14, 16)) < 60;
}, 'Введите корректные дату и время');

// Form dates use the organization's timezone, regardless of the browser's timezone.
export const toLocalDateTimeInput = (value: string | Date = new Date()) => {
  const date = new Date(value);
  const parts = new Intl.DateTimeFormat('en-GB', { timeZone: 'Asia/Yekaterinburg', year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }).formatToParts(date);
  const part = (type: string) => parts.find(p => p.type === type)!.value;
  return `${part('year').padStart(4, '0')}-${part('month')}-${part('day')}T${part('hour')}:${part('minute')}`;
};
export const dateTimeToIso = (value: string) => new Date(`${dateTimeSchema.parse(value)}:00+05:00`).toISOString();

export const fieldErrors = (result: z.SafeParseReturnType<unknown, unknown>) => {
  if (result.success) return {} as Record<string, string>;
  const errors: Record<string, string> = {};
  for (const issue of result.error.issues) {
    const key = String(issue.path[0] ?? 'form');
    errors[key] ??= issue.message;
  }
  return errors;
};

export const loginSchema = z.object({
  login: requiredText('Логин', 100),
  password: z.string().min(1, 'Введите пароль'),
});

export const assetSchema = z.object({
  inventoryNumber: requiredText('Инвентарный номер', 100),
  name: requiredText('Наименование', 500),
  accountingAccount: requiredText('Бухгалтерский счёт', 50),
  serialNumber: optionalText(200),
  manufactureYear: z.number({ invalid_type_error: 'Введите год числом' }).int('Год должен быть целым числом').min(1900, 'Год не может быть раньше 1900').max(new Date().getFullYear() + 1, 'Год не может быть позже следующего года').nullable().optional(),
  acceptedAt: dateSchema,
  initialCost: z.string().trim().min(1, 'Введите стоимость числом')
    .refine(value => !value.startsWith('-'), 'Стоимость не может быть отрицательной')
    .refine(value => /^\d+(?:[.,]\d{1,2})?$/.test(value), 'Введите число с точностью до двух знаков после запятой')
    .transform(value => value.replace(',', '.').replace(/^0+(?=\d)/, ''))
    .refine(value => value.split('.')[0].length <= 17, 'Укажите не более 17 цифр до запятой'),
  departmentId: uuid('подразделение'),
  locationId: uuid('помещение'),
  materiallyResponsibleEmployeeId: uuid('материально ответственное лицо'),
});

export const organizationSchema = z.object({
  name: requiredText('Наименование', 300),
  inn: z.string().trim().regex(/^(?:\d{10}|\d{12})$/, 'ИНН должен содержать 10 или 12 цифр'),
  kpp: z.string().trim().refine(value => !value || /^\d{9}$/.test(value), 'КПП должен содержать 9 цифр').nullable().optional(),
  okpo: z.string().trim().refine(value => !value || /^(?:\d{8}|\d{10})$/.test(value), 'ОКПО должен содержать 8 или 10 цифр').nullable().optional(),
  address: optionalText(500),
  directorName: optionalText(300),
});

export const departmentSchema = z.object({ code: requiredText('Код', 50), name: requiredText('Название', 250) });
export const locationSchema = departmentSchema.extend({ departmentId: uuid('подразделение') });
export const employeeSchema = z.object({ personnelNumber: requiredText('Табельный номер', 50), fullName: requiredText('ФИО', 300), departmentId: uuid('подразделение') });

export const assigneeSchema = z.object({ oldUserId: uuid('текущего исполнителя'), newUserId: uuid('нового исполнителя') })
  .refine(value => value.oldUserId !== value.newUserId, { path: ['newUserId'], message: 'Выберите другого исполнителя' });

export const userSchema = z.object({
  login: requiredText('Логин', 100).regex(/^[\p{L}\p{N}._@-]+$/u, 'Допустимы буквы, цифры и символы . _ @ -'),
  password: z.string().min(8, 'Минимум 8 символов').max(256, 'Не более 256 символов').refine(value => value.trim().length > 0, 'Пароль не должен состоять из пробелов'),
  confirm: z.string(),
  role: z.enum(['Administrator', 'InventoryEmployee']),
  employeeId: z.union([z.literal(''), z.string().uuid('Выберите сотрудника')]),
}).refine(value => value.password === value.confirm, { path: ['confirm'], message: 'Пароли не совпадают' })
  .refine(value => value.role !== 'InventoryEmployee' || value.employeeId !== '', { path: ['employeeId'], message: 'Выберите сотрудника отдела инвентаризации' });

export const scheduleSchema = z.object({
  name: requiredText('Название', 200),
  intervalMonths: z.number({ invalid_type_error: 'Введите интервал числом' }).int('Интервал должен быть целым').min(1, 'Минимум 1 месяц').max(1200, 'Не более 1200 месяцев'),
  nextRunAt: dateTimeSchema,
  launchMode: z.enum(['CreateDraft', 'AutoStart']),
});

export const eventSchema = z.object({ eventId: requiredText('Event ID', 200), employeeId: uuid('сотрудника'), occurredAt: dateTimeSchema, details: z.string().trim().max(1000, 'Не более 1000 символов') });

export const inventorySchema = z.object({
  type: z.enum(['Planned', 'EventDriven', 'Manual']),
  reason: z.enum(['AnnualReporting', 'MolChange', 'MolDismissal', 'AssetTransfer', 'LossOrDamage', 'Emergency', 'Reorganization', 'Liquidation', 'Other']),
  reasonDetails: z.string().trim().max(1000, 'Не более 1000 символов'),
  targetEmployeeId: z.string(),
  plannedAt: dateTimeSchema,
}).superRefine((value, ctx) => {
  if (value.reason === 'Other' && !value.reasonDetails) ctx.addIssue({ code: z.ZodIssueCode.custom, path: ['reasonDetails'], message: 'Опишите основание' });
  if (value.type === 'EventDriven' && !z.string().uuid().safeParse(value.targetEmployeeId).success) ctx.addIssue({ code: z.ZodIssueCode.custom, path: ['targetEmployeeId'], message: 'Выберите материально ответственное лицо' });
});
export const cloneInventorySchema = z.object({ plannedAt: dateTimeSchema });
