import { createTheme } from '@mui/material/styles';

export const theme = createTheme({
  palette: {
    mode: 'light',
    primary: { main: '#0b4f8a', dark: '#08365f', light: '#dcecf8' },
    secondary: { main: '#0f8f7b' },
    background: { default: '#f3f6f9', paper: '#ffffff' },
    text: { primary: '#162332', secondary: '#526474' },
    divider: '#d8e0e8',
  },
  typography: { fontFamily: 'Inter, Segoe UI, Arial, sans-serif', fontSize: 14, h4: { fontWeight: 700 }, h5: { fontWeight: 700 }, h6: { fontWeight: 650 } },
  shape: { borderRadius: 8 },
  components: {
    MuiButton: { defaultProps: { disableElevation: true }, styleOverrides: { root: { textTransform: 'none', fontWeight: 600, minHeight: 38 } } },
    MuiPaper: { defaultProps: { elevation: 0 }, styleOverrides: { root: { border: '1px solid #d8e0e8' } } },
    MuiTableCell: { styleOverrides: { root: { padding: '10px 12px' }, head: { background: '#eef3f7', color: '#34495c', fontWeight: 700, whiteSpace: 'nowrap' } } },
    MuiTextField: { defaultProps: { size: 'small' } }, MuiSelect: { defaultProps: { size: 'small' } },
  },
});
