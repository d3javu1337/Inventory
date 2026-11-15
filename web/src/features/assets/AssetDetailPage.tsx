import { Download, Edit, Print } from "@mui/icons-material";
import {
  Button,
  Alert,
  Grid,
  Paper,
  Stack,
  TableCell,
  TableRow,
  Typography,
} from "@mui/material";
import { useQuery } from "@tanstack/react-query";
import { Link, useParams } from "react-router-dom";
import { api } from "../../api/api";
import { useFileAction } from "../../components/useFileAction";
import {
  DataTable,
  ErrorState,
  LoadingState,
  PageHeader,
  PrimaryWithId,
  StatusChip,
} from "../../components/Ui";
import {
  formatDate,
  formatDateTime,
  formatMoney,
} from "../../shared/formatters";
import { queryKeys } from "../../shared/queryKeys";
import { usePagedList } from '../../components/usePagedList';
import type { AssetHistory } from '../../api/types';
import { AssetQrPreview } from './AssetQrPreview';
const Field = ({ name, value }: { name: string; value: React.ReactNode }) => (
  <Stack>
    <Typography variant="caption" color="text.secondary">
      {name}
    </Typography>
    <Typography>{value || "—"}</Typography>
  </Stack>
);
export function AssetDetailPage() {
  const file = useFileAction();
  const { id = "" } = useParams();
  const q = useQuery({
    queryKey: queryKeys.asset(id),
    queryFn: () => api.asset(id),
  });
  const history = usePagedList<AssetHistory>(queryKeys.assetHistory(id), `assets/${id}/history`);
  if (q.isLoading) return <LoadingState />;
  if (q.error) return <ErrorState error={q.error} />;
  const x = q.data!.data;
  return (
    <>
      <PageHeader
        title={`${x.inventoryNumber} — ${x.name}`}
        action={
          <Stack direction="row" gap={1} flexWrap="wrap">
            <Button component={Link} to={`/audit?entityType=Asset&entityId=${id}`}>История действий</Button>
            <Button
              component={Link}
              disabled={!!q.data?.data.archivedAt || q.data?.data.status === "Archived"} to={`/assets/${id}/edit`}
              startIcon={<Edit />}
            >
              Изменить
            </Button>
            <Button
              startIcon={<Download />}
              variant="contained"
              disabled={file.pending} onClick={() => void file.run(`/api/v1/assets/${id}/qr`)}
            >
              Скачать QR
            </Button>
            <Button
              startIcon={<Print />}
              disabled={file.pending} onClick={() => void file.run(`/api/v1/assets/${id}/qr`, true)}
            >
              Печать
            </Button>
          </Stack>
        }
      />
      {file.error && <Alert severity="error" sx={{ mb: 2 }}>{file.error}</Alert>}
      <Stack direction={{ xs: 'column', lg: 'row' }} gap={2} alignItems="flex-start" mb={2}>
      <Paper sx={{ p: 3, width: '100%', flex: 1, minWidth: 0 }}>
        <Grid container spacing={3}>
          <Grid item xs={12} sm={6} md={3}>
            <Field name="Статус" value={<StatusChip value={x.status} />} />
          </Grid>
          <Grid item xs={12} sm={6} md={3}>
            <Field name="Бухгалтерский счет" value={x.accountingAccount} />
          </Grid>
          <Grid item xs={12} sm={6} md={3}>
            <Field name="Стоимость" value={formatMoney(x.initialCostExact ?? x.initialCost)} />
          </Grid>
          <Grid item xs={12} sm={6} md={3}>
            <Field name="Дата принятия" value={formatDate(x.acceptedAt)} />
          </Grid>
          <Grid item xs={12} sm={6} md={4}>
            <Field name="Подразделение" value={x.departmentName} />
          </Grid>
          <Grid item xs={12} sm={6} md={4}>
            <Field name="Помещение" value={x.locationName} />
          </Grid>
          <Grid item xs={12} sm={6} md={4}>
            <Field name="МОЛ" value={x.materiallyResponsibleEmployeeName} />
          </Grid>
          <Grid item xs={12} sm={6} md={3}>
            <Field name="Серийный номер" value={x.serialNumber} />
          </Grid>
          <Grid item xs={12} sm={6} md={3}>
            <Field name="Год выпуска" value={x.manufactureYear} />
          </Grid>
          <Grid item xs={12} sm={6} md={3}>
            <Field name="Версия" value={x.version} />
          </Grid>
          <Grid item xs={12} sm={6} md={3}>
            <Field name="Архивировано" value={formatDateTime(x.archivedAt)} />
          </Grid>
        </Grid>
      </Paper>
        <AssetQrPreview asset={x} />
      </Stack>
      <Typography variant="h6" mb={1.5}>
        История назначений
      </Typography>
      {history.controls}
      {history.isLoading ? (
        <LoadingState />
      ) : history.error ? (
        <ErrorState error={history.error} retry={() => void history.refetch()} />
      ) : (
        <DataTable
          headers={["С", "По", "Подразделение", "Помещение", "МОЛ"]}
          empty={!history.data?.length}
        >
          {history.data?.map((h) => (
            <TableRow key={h.id}>
              <TableCell>{formatDateTime(h.validFrom)}</TableCell>
              <TableCell>{formatDateTime(h.validTo)}</TableCell>
              <TableCell>
                <PrimaryWithId primary={h.departmentName} id={h.departmentId} />
              </TableCell>
              <TableCell>
                <PrimaryWithId primary={h.locationName} id={h.locationId} />
              </TableCell>
              <TableCell>
                <PrimaryWithId
                  primary={h.materiallyResponsibleEmployeeName}
                  id={h.materiallyResponsibleEmployeeId}
                />
              </TableCell>
            </TableRow>
          ))}
        </DataTable>
      )}
    </>
  );
}
