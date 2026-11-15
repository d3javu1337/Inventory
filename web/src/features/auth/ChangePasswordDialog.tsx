import { Alert, Button, Dialog, DialogActions, DialogContent, DialogTitle, Stack, TextField, Typography } from '@mui/material';
import { useRef, useState, type FormEvent } from 'react';
import { useAuth } from '../../auth/AuthProvider';

export function ChangePasswordDialog({ onClose }: { onClose: () => void }) {
  const { changePassword } = useAuth();
  const [current, setCurrent] = useState('');
  const [password, setPassword] = useState('');
  const [confirmation, setConfirmation] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState(false);
  const pending = useRef(false);
  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (pending.current || done) return;
    setError('');
    if (!current) return setError('Введите текущий пароль.');
    if (password.trim().length === 0 || password.length < 8 || password.length > 256) return setError('Новый пароль должен содержать от 8 до 256 символов.');
    if (password === current) return setError('Новый пароль должен отличаться от текущего.');
    if (password !== confirmation) return setError('Пароли не совпадают.');
    pending.current = true;
    setBusy(true);
    try {
      await changePassword(current, password);
      setCurrent(''); setPassword(''); setConfirmation('');
      setDone(true);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось изменить пароль. Повторите попытку.');
    } finally { pending.current = false; setBusy(false); }
  };
  return <Dialog open onClose={() => !pending.current && onClose()} fullWidth maxWidth="xs">
    <form onSubmit={submit} noValidate>
      <DialogTitle>Смена пароля</DialogTitle>
      <DialogContent>
        {done ? <Alert severity="success">Пароль изменён. На остальных устройствах потребуется войти заново.</Alert> : <Stack spacing={2} pt={1}>
          <Typography color="text.secondary">После смены пароля остальные сеансы будут завершены.</Typography>
          {error && <Alert severity="error">{error}</Alert>}
          <TextField label="Текущий пароль" type="password" autoComplete="current-password" autoFocus disabled={busy} value={current} onChange={e => setCurrent(e.target.value)} />
          <TextField label="Новый пароль" type="password" autoComplete="new-password" inputProps={{ maxLength: 256 }} disabled={busy} value={password} onChange={e => setPassword(e.target.value)} />
          <TextField label="Повторите новый пароль" type="password" autoComplete="new-password" inputProps={{ maxLength: 256 }} disabled={busy} value={confirmation} onChange={e => setConfirmation(e.target.value)} />
        </Stack>}
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose} disabled={busy}>{done ? 'Закрыть' : 'Отмена'}</Button>
        {!done && <Button type="submit" variant="contained" disabled={busy}>{busy ? 'Сохранение…' : 'Изменить пароль'}</Button>}
      </DialogActions>
    </form>
  </Dialog>;
}
