import { LockOutlined } from '@mui/icons-material';
import { Alert, Avatar, Box, Button, CircularProgress, Paper, Stack, TextField, Typography } from '@mui/material';
import { type FormEvent, useRef, useState } from 'react';
import { Navigate, useLocation, useNavigate, useSearchParams } from 'react-router-dom';
import { useAuth } from '../../auth/AuthProvider';

type LoginStep = 'login' | 'password' | 'initial-password';

export function LoginPage() {
  const { user, loading, authError, startLogin, login, setInitialPassword } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const [params] = useSearchParams();
  const [step, setStep] = useState<LoginStep>('login');
  const [loginValue, setLoginValue] = useState('');
  const [password, setPassword] = useState('');
  const [confirmation, setConfirmation] = useState('');
  const [error, setError] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const pending = useRef(false);

  if (loading) return <Box minHeight="100vh" display="grid" sx={{ placeItems: 'center' }}><CircularProgress /></Box>;
  if (user?.role === 'Administrator') return <Navigate to="/dashboard" replace />;

  const finishLogin = () => {
    const from = (location.state as { from?: { pathname?: string } })?.from?.pathname;
    navigate(from || '/dashboard', { replace: true });
  };

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (pending.current) return;
    setError('');
    const normalizedLogin = loginValue.trim();
    if (!normalizedLogin) return setError('Введите логин');
    if (normalizedLogin.length > 100) return setError('Логин должен содержать не более 100 символов');
    if (step === 'password' && !password) return setError('Введите пароль');
    if (step === 'initial-password' && password.length < 8) return setError('Пароль должен содержать минимум 8 символов');
    if (step === 'initial-password' && password !== confirmation) return setError('Пароли не совпадают');

    pending.current = true;
    setSubmitting(true);
    try {
      if (step === 'login') {
        setStep(await startLogin(normalizedLogin) ? 'initial-password' : 'password');
      } else if (step === 'initial-password') {
        await setInitialPassword(normalizedLogin, password);
        finishLogin();
      } else {
        await login(normalizedLogin, password);
        finishLogin();
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось войти');
    } finally {
      pending.current = false;
      setSubmitting(false);
    }
  };

  const resetLogin = () => {
    setStep('login');
    setPassword('');
    setConfirmation('');
    setError('');
  };

  return (
    <Box minHeight="100vh" display="grid" sx={{ placeItems: 'center', p: 2, background: 'linear-gradient(135deg,#0a2b50 0%,#0b4f8a 55%,#0e6f78 100%)' }}>
      <Paper component="form" onSubmit={submit} sx={{ width: '100%', maxWidth: 420, p: { xs: 3, sm: 4 }, boxShadow: '0 24px 60px #001b3640' }}>
        <Stack alignItems="center" spacing={2.5}>
          <Avatar sx={{ bgcolor: 'primary.main', width: 48, height: 48 }}><LockOutlined /></Avatar>
          <Box textAlign="center">
            <Typography variant="h5">Asset Inventory</Typography>
            <Typography color="text.secondary">
              {step === 'initial-password' ? 'Придумайте пароль администратора' : 'Административная панель'}
            </Typography>
          </Box>
          {params.get('error') === 'role' && <Alert severity="warning" sx={{ width: '100%' }}>Доступ разрешен только администратору</Alert>}
          {(error || authError) && <Alert severity="error" sx={{ width: '100%' }}>{error || authError}</Alert>}
          <TextField
            fullWidth
            label="Логин"
            required
            inputProps={{ maxLength: 100 }}
            autoComplete="username"
            autoFocus
            value={loginValue}
            disabled={step !== 'login' || submitting}
            onChange={event => setLoginValue(event.target.value)}
          />
          {step !== 'login' && <TextField
            fullWidth
            label={step === 'initial-password' ? 'Новый пароль' : 'Пароль'}
            type="password"
            required
            autoComplete={step === 'initial-password' ? 'new-password' : 'current-password'}
            disabled={submitting}
            autoFocus
            value={password}
            onChange={event => setPassword(event.target.value)}
          />}
          {step === 'initial-password' && <TextField
            fullWidth
            label="Повторите пароль"
            type="password"
            autoComplete="new-password"
            disabled={submitting}
            value={confirmation}
            onChange={event => setConfirmation(event.target.value)}
          />}
          <Button fullWidth type="submit" variant="contained" size="large" disabled={submitting}>
            {submitting ? <CircularProgress size={22} color="inherit" /> : step === 'login' ? 'Продолжить' : step === 'initial-password' ? 'Установить пароль' : 'Войти'}
          </Button>
          {step !== 'login' && <Button disabled={submitting} onClick={resetLogin}>Другой логин</Button>}
        </Stack>
      </Paper>
    </Box>
  );
}
