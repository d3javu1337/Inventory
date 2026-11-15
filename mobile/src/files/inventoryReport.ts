import { apiFetchBlob } from '../api/client';
import { session } from '../auth/session';
import { saveReportFile } from './saveReportFile';

export type ReportFormat = 'pdf' | 'xlsx';
export async function downloadInventoryReport(id: string, number: string, format: ReportFormat): Promise<boolean> {
  const revision = session.getRevision();
  const blob = await apiFetchBlob(`/inventories/${encodeURIComponent(id)}/reports/inv-1.${format}`);
  const mime = format === 'pdf' ? 'application/pdf' : 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet';
  const fileName = `INV-1-${number.replace(/[\\/:*?"<>|]/g, '_')}.${format}`;
  return saveReportFile(blob, fileName, mime, () => revision === session.getRevision());
}
