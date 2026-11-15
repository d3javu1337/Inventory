import { Alert, Box, Button, Paper, Skeleton, Typography } from '@mui/material';
import { useQuery } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { requestBlob } from '../../api/httpClient';
import type { Asset } from '../../api/types';

export function AssetQrPreview({ asset }: { asset: Pick<Asset, 'id' | 'inventoryNumber' | 'version'> }) {
  const qr = useQuery({
    queryKey: ['assetQr', asset.id, asset.version],
    queryFn: ({ signal }) => requestBlob(`/api/v1/assets/${asset.id}/qr`, signal),
    retry: false,
  });
  const [image, setImage] = useState<{ blob: Blob; url: string }>();

  useEffect(() => {
    if (!qr.data) return;
    const url = URL.createObjectURL(qr.data);
    setImage({ blob: qr.data, url });
    return () => URL.revokeObjectURL(url);
  }, [qr.data]);

  return (
    <Paper component="section" aria-label="QR-этикетка" sx={{ p: 2, width: { xs: '100%', lg: 280 }, flexShrink: 0 }}>
      <Typography component="h2" variant="h6">QR-этикетка</Typography>
      <Typography variant="body2" color="text.secondary" mb={1}>
        Можно сканировать прямо с экрана.
      </Typography>
      {image && image.blob === qr.data ? (
        <Box component="img" src={image.url} alt={`QR-код ОС ${asset.inventoryNumber}`}
          sx={{ display: 'block', width: 240, maxWidth: '100%', height: 'auto', mx: 'auto' }} />
      ) : qr.error ? (
        <Alert severity="error" action={<Button color="inherit" size="small" disabled={qr.isFetching} onClick={() => void qr.refetch()}>Повторить</Button>}>
          Не удалось загрузить QR-код.
        </Alert>
      ) : (
        <Skeleton variant="rounded" sx={{ width: 240, maxWidth: '100%', height: 240, mx: 'auto' }} />
      )}
    </Paper>
  );
}
