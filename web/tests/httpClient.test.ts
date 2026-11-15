import { describe, expect, it } from 'vitest'; import { ApiError } from '../src/api/httpClient';
describe('ApiError',()=>{it('сохраняет traceId и понятный заголовок',()=>{const e=new ApiError(409,{title:'Конфликт',traceId:'trace-1'});expect(e.message).toBe('Конфликт');expect(e.problem.traceId).toBe('trace-1')})});
