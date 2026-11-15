import { CloudUpload, Download } from "@mui/icons-material";
import {
  Alert,
  Button,
  LinearProgress,
  Paper,
  Stack,
  TableCell,
  TableRow,
  Typography,
} from "@mui/material";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { request } from "../../api/httpClient";
import { useFileAction } from "../../components/useFileAction";
import { DataTable, PageHeader } from "../../components/Ui";
import type { ImportResult } from "../../api/types";
import { downloadImportErrors } from "./importErrors";
export function ImportPage() {
  const template = useFileAction();
  const [file, setFile] = useState<File | null>(null);
  const [result, setResult] = useState<ImportResult | null>(null);
  const [error, setError] = useState("");
  const nav = useNavigate();
  const qc = useQueryClient();
  const run = useMutation({
    onMutate: (apply: boolean) => {
      setError("");
      if (!apply) setResult(null);
    },
    mutationFn: async (apply: boolean) => {
      if (!file) throw new Error("Выберите файл");
      const body = new FormData();
      body.append("file", file);
      return request<ImportResult>(`/api/v1/assets/imports?apply=${apply}`, {
        method: "POST",
        body,
      });
    },
    onSuccess: (r, apply) => {
      setResult(r);
      if (apply) {
        void qc.invalidateQueries({ queryKey: ["assets"] });
        void qc.invalidateQueries({ queryKey: ["dashboard"] });
      }
    },
    onError: (e) => setError(e instanceof Error ? e.message : "Ошибка импорта"),
  });
  const choose = (f?: File) => {
    setError("");
    setResult(null);
    setFile(null);
    if (!f) return;
    if (!/\.(csv|xlsx)$/i.test(f.name)) {
      setError("Поддерживаются только CSV и XLSX");
      return;
    }
    if (f.size === 0) {
      setError("Файл пуст");
      return;
    }
    if (f.size > 20 * 1024 * 1024) {
      setError("Размер файла превышает 20 МБ");
      return;
    }
    setFile(f);
  };
  return (
    <>
      <PageHeader
        title="Импорт основных средств"
        description="Сначала проверьте файл, затем подтвердите применение"
        action={
          <Button
            startIcon={<Download />}
            disabled={template.pending} onClick={() => void template.run("/api/v1/assets/imports/template.csv")}
          >
            Скачать шаблон
          </Button>
        }
      />
      {template.error && <Alert severity="error" sx={{ mb: 2 }}>{template.error}</Alert>}
      <Paper sx={{ p: 3, maxWidth: 900 }}>
        <Stack spacing={2}>
          <Button
            disabled={run.isPending}
            component="label"
            variant="outlined"
            startIcon={<CloudUpload />}
          >
            Выбрать CSV или XLSX
            <input
              disabled={run.isPending}
              hidden
              type="file"
              accept=".csv,.xlsx"
              onChange={(e) => choose(e.target.files?.[0])}
            />
          </Button>
          <Typography variant="body2" color="text.secondary">
            В XLSX укажите стоимость с 14 и более цифрами до запятой в текстовой ячейке, чтобы сохранить копейки.
          </Typography>
          {file && (
            <Typography>
              Файл: <b>{file.name}</b> · {(file.size / 1024).toFixed(1)} КБ
            </Typography>
          )}
          {error && <Alert severity="error">{error}</Alert>}
          {run.isPending && <LinearProgress />}
          <Stack direction="row" gap={1}>
            <Button
              variant="contained"
              disabled={!file || run.isPending}
              onClick={() => run.mutate(false)}
            >
              Проверить файл
            </Button>
            {result && result.invalidRows === 0 && result.appliedRows === 0 && (
              <Button
                color="success"
                variant="contained"
                disabled={run.isPending}
                onClick={() => run.mutate(true)}
              >
                Применить импорт
              </Button>
            )}
            <Button onClick={() => nav("/assets")}>К реестру</Button>
          </Stack>
          {result && (
            <>
              <Stack direction={{ xs: "column", sm: "row" }} gap={3}>
                <Typography>
                  Всего строк: <b>{result.totalRows}</b>
                </Typography>
                <Typography color="success.main">
                  Корректных: <b>{result.validRows}</b>
                </Typography>
                <Typography
                  color={result.invalidRows ? "error.main" : "text.secondary"}
                >
                  Ошибок: <b>{result.invalidRows}</b>
                </Typography>
                {result.appliedRows > 0 && (
                  <Typography>
                    Применено: <b>{result.appliedRows}</b>
                  </Typography>
                )}
              </Stack>
              {result.rows.some((x) => !x.isValid) && (
                <Button startIcon={<Download />} onClick={() => downloadImportErrors(result)}>Скачать ошибки CSV</Button>
              )}
              {result.rows.some((x) => !x.isValid) && (
                <DataTable key={result.importId} headers={["Строка", "Инвентарный номер", "Ошибка"]}>
                  {result.rows
                    .filter((x) => !x.isValid)
                    .map((x) => (
                      <TableRow key={x.rowNumber}>
                        <TableCell>{x.rowNumber}</TableCell>
                        <TableCell>{x.inventoryNumber}</TableCell>
                        <TableCell>{x.error}</TableCell>
                      </TableRow>
                    ))}
                </DataTable>
              )}
            </>
          )}
        </Stack>
      </Paper>
    </>
  );
}
