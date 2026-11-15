import {
  Alert,
  Box,
  Button,
  Chip,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Paper,
  Skeleton,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  TablePagination,
  Typography,
} from "@mui/material";
import type { ReactNode } from "react";
import { Children, useState } from 'react';
import { ApiError } from "../api/httpClient";
import { label } from "../shared/labels";

export function PageHeader({
  title,
  action,
  description,
}: {
  title: string;
  action?: ReactNode;
  description?: string;
}) {
  return (
    <Stack
      direction={{ xs: "column", sm: "row" }}
      justifyContent="space-between"
      alignItems={{ sm: "center" }}
      gap={1.5}
      mb={2.5}
    >
      <Box>
        <Typography variant="h5">{title}</Typography>
        {description && (
          <Typography color="text.secondary">{description}</Typography>
        )}
      </Box>
      {action}
    </Stack>
  );
}
export function LoadingState() {
  return (
    <Stack spacing={1}>
      <Skeleton height={48} />
      <Skeleton height={48} />
      <Skeleton height={48} />
    </Stack>
  );
}
export function ErrorState({
  error,
  retry,
}: {
  error: unknown;
  retry?: () => void;
}) {
  const problem = error instanceof ApiError ? error.problem : undefined;
  return (
    <Alert
      severity="error"
      action={
        retry && (
          <Button color="inherit" onClick={retry}>
            Повторить
          </Button>
        )
      }
    >
      <b>
        {error instanceof Error ? error.message : "Не удалось загрузить данные"}
      </b>
      {problem?.traceId && (
        <Box component="span" display="block">
          Код обращения: {problem.traceId}
        </Box>
      )}
    </Alert>
  );
}
export function EmptyState({ text = "Нет данных" }: { text?: string }) {
  return (
    <Box py={6} textAlign="center" color="text.secondary">
      {text}
    </Box>
  );
}
export function StatusChip({ value }: { value: string }) {
  const color =
    (
      {
        Completed: "success",
        Active: "primary",
        NeedsAttention: "warning",
        ReviewRequired: "error",
        Failed: "error",
        Blocked: "error",
        Cancelled: "default",
        Archived: "default",
      } as const
    )[value] ?? "default";
  return (
    <Chip
      size="small"
      label={label(value)}
      color={color}
      variant={color === "default" ? "outlined" : "filled"}
    />
  );
}
export function PrimaryWithId({
  primary,
  id,
}: {
  primary: ReactNode;
  id?: string | null;
}) {
  return (
    <Stack spacing={0.25} minWidth={0}>
      <Typography component="span">{primary || "—"}</Typography>
      {id && (
        <Typography
          component="span"
          variant="caption"
          color="text.secondary"
          sx={{ fontFamily: "monospace", overflowWrap: "anywhere" }}
        >
          {id}
        </Typography>
      )}
    </Stack>
  );
}
export function ConfirmDialog({
  open,
  title,
  text,
  busy,
  error,
  onCancel,
  onConfirm,
}: {
  open: boolean;
  title: string;
  text: string;
  busy?: boolean;
  error?: Error | null;
  onCancel: () => void;
  onConfirm: () => void;
}) {
  return (
    <Dialog open={open} onClose={busy ? undefined : onCancel} maxWidth="xs" fullWidth>
      <DialogTitle>{title}</DialogTitle>
      <DialogContent>
        <Typography>{text}</Typography>
        {error && <Alert severity="error" sx={{ mt: 2 }}>{error.message}</Alert>}
      </DialogContent>
      <DialogActions>
        <Button onClick={onCancel} disabled={busy}>Отмена</Button>
        <Button
          color="error"
          variant="contained"
          disabled={busy}
          onClick={onConfirm}
        >
          {busy ? <CircularProgress size={20} /> : "Подтвердить"}
        </Button>
      </DialogActions>
    </Dialog>
  );
}
export function DataTable({
  headers,
  children,
  empty,
}: {
  headers: string[];
  children: ReactNode;
  empty?: boolean;
}) {
  const [page, setPage] = useState(0);
  const rows = Children.toArray(children);
  const currentPage = Math.min(page, Math.max(0, Math.ceil(rows.length / 50) - 1));
  // Server-paged lists have at most 50 rows. This fallback bounds local import previews.
  const visible = rows.length > 50 ? rows.slice(currentPage * 50, (currentPage + 1) * 50) : rows;
  return (
    <TableContainer component={Paper}>
      <Table size="small">
        <TableHead>
          <TableRow>
            {headers.map((h) => (
              <TableCell key={h}>{h}</TableCell>
            ))}
          </TableRow>
        </TableHead>
        <TableBody>{visible}</TableBody>
      </Table>
      {empty && <EmptyState />}
      {rows.length > 50 && <TablePagination component="div" count={rows.length} page={currentPage} rowsPerPage={50} rowsPerPageOptions={[50]} onPageChange={(_, value) => setPage(value)} labelDisplayedRows={({ from, to, count }) => `${from}–${to} из ${count}`} getItemAriaLabel={type => type === 'next' ? 'Следующая страница' : 'Предыдущая страница'} />}
    </TableContainer>
  );
}
