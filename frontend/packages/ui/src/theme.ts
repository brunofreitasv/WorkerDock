import { createTheme } from "@mui/material";

export const workerdockTheme = createTheme({
  palette: {
    primary: { main: "#0B6C61", dark: "#064F47", light: "#DFF2ED" },
    background: { default: "#F5F8F7", paper: "#FFFFFF" },
    text: { primary: "#182D39", secondary: "#536772" },
  },
  shape: { borderRadius: 12 },
  typography: {
    fontFamily: 'system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif',
    h4: { fontWeight: 700, letterSpacing: "-.035em" },
    button: { textTransform: "none", fontWeight: 600 },
  },
  components: {
    MuiCssBaseline: { styleOverrides: { body: { margin: 0 } } },
    MuiPaper: { styleOverrides: { root: { backgroundImage: "none" } } },
    MuiButton: { defaultProps: { disableElevation: true } },
    MuiButtonBase: { styleOverrides: { root: { "&:focus-visible": { outline: "3px solid #0B6C61", outlineOffset: 2 } } } },
    MuiLink: { styleOverrides: { root: { "&:focus-visible": { outline: "3px solid #0B6C61", outlineOffset: 2 } } } },
  },
});
