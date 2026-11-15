import { Box, CircularProgress } from '@mui/material';
import { Navigate, Outlet, useLocation } from 'react-router-dom';
import { useAuth } from './AuthProvider';

export function RequireAuth() {
  const { user, loading } = useAuth(); const location = useLocation();
  if (loading) return <Box minHeight="100vh" display="grid" sx={{ placeItems: 'center' }}><CircularProgress /></Box>;
  if (!user) return <Navigate to="/login" state={{ from: location }} replace />;
  if (user.role !== 'Administrator') return <Navigate to="/login?error=role" replace />;
  return <Outlet />;
}
