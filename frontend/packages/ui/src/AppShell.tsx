import { useRef, type ReactNode } from "react";
import {
  Alert,
  AppBar,
  Box,
  Button,
  Chip,
  Divider,
  Link,
  Paper,
  Stack,
  Toolbar,
  Typography,
} from "@mui/material";
import type { Labels } from "./preferences";
import { BrandMark } from "./BrandMark";
interface Props {
  children: ReactNode;
  t: Labels;
  navigation: { id: string; label: string }[];
  active: string;
  onNavigate: (id: string) => void;
  mode: string;
  canManage: boolean;
  authenticated: boolean;
  onSignOut: () => void;
}
export function AppShell({
  children,
  t,
  navigation,
  active,
  onNavigate,
  mode,
  canManage,
  authenticated,
  onSignOut,
}: Props) {
  const main = useRef<HTMLElement>(null);
  return (
    <Box sx={{ minHeight: "100vh", bgcolor: "background.default" }}>
      <Link
        href="#main"
        onClick={(e) => {
          e.preventDefault();
          main.current?.focus();
        }}
        sx={{
          position: "fixed",
          top: -60,
          left: 16,
          zIndex: 1500,
          bgcolor: "background.paper",
          p: 2,
          "&:focus": { top: 8 },
        }}
      >
        {t.skip}
      </Link>
      <AppBar
        position="static"
        color="inherit"
        elevation={0}
        sx={{ borderBottom: "1px solid", borderColor: "divider" }}
      >
        <Toolbar sx={{ gap: 2 }}>
          <BrandMark size={36} />
          <Typography
            component="span"
            variant="h6"
            sx={{ fontWeight: 800, letterSpacing: "-.04em" }}
          >
            WorkerDock
          </Typography>
          <Typography
            variant="body2"
            sx={{
              display: { xs: "none", sm: "block" },
              color: "text.secondary",
              flex: 1,
            }}
          >
            {t.tenant}
          </Typography>
          {authenticated && (
            <Button onClick={onSignOut} sx={{ ml: "auto" }}>
              {t.signOut}
            </Button>
          )}
          {mode !== "Anonymous" && !authenticated && (
            <Button
              component="a"
              href="/auth/signin"
              variant="contained"
              sx={{ ml: "auto" }}
            >
              {t.signIn}
            </Button>
          )}
        </Toolbar>
      </AppBar>
      <Box
        sx={{
          display: { xs: "block", md: "grid" },
          gridTemplateColumns: "224px minmax(0,1fr)",
          maxWidth: 1600,
          mx: "auto",
        }}
      >
        <Box
          component="aside"
          sx={{
            p: { xs: 2, md: 3 },
            borderRight: { md: "1px solid" },
            borderColor: "divider",
            minHeight: { md: "calc(100vh - 65px)" },
          }}
        >
          <Typography variant="overline" color="text.secondary">
            {t.overview}
          </Typography>
          <Stack
            component="nav"
            aria-label={t.tenant}
            direction={{ xs: "row", md: "column" }}
            sx={{ gap: 0.5, mt: 1, flexWrap: "wrap" }}
          >
            {navigation.map((item) => (
              <Button
                key={item.id}
                component="a"
                href={"#/" + item.id}
                aria-current={active === item.id ? "page" : undefined}
                onClick={(e) => {
                  e.preventDefault();
                  onNavigate(item.id);
                }}
                sx={{
                  justifyContent: "flex-start",
                  px: 2,
                  py: 1.2,
                  color: active === item.id ? "primary.dark" : "text.secondary",
                  bgcolor: active === item.id ? "primary.light" : "transparent",
                  fontWeight: active === item.id ? 700 : 500,
                }}
              >
                {item.label}
              </Button>
            ))}
          </Stack>
          <Divider sx={{ my: 3, display: { xs: "none", md: "block" } }} />
          <Typography
            variant="caption"
            color="text.secondary"
            sx={{ display: { xs: "none", md: "block" } }}
          >
            WorkerDock · 0.1
          </Typography>
        </Box>
        <Box
          component="main"
          id="main"
          ref={main}
          tabIndex={-1}
          sx={{ minWidth: 0, p: { xs: 2, sm: 3, lg: 5 }, outlineOffset: -4 }}
        >
          <Paper variant="outlined" sx={{ p: 2, mb: 4, borderRadius: 2 }}>
            <Stack
              direction={{ xs: "column", sm: "row" }}
              spacing={1.5}
              sx={{
                alignItems: { sm: "center" },
                justifyContent: "space-between",
              }}
            >
              <Box>
                <Typography variant="overline" color="text.secondary">
                  {t.access}
                </Typography>
                <Typography
                  role="status"
                  variant="body2"
                  sx={{ fontWeight: 700 }}
                >
                  {mode === "Anonymous"
                    ? t.anonymous
                    : mode === "RoleProtected"
                      ? t.roleProtected
                      : t.authenticated}
                </Typography>
              </Box>
              <Chip
                label={canManage ? t.manage : t.readOnly}
                size="small"
                color={canManage ? "primary" : "default"}
                variant="outlined"
              />
            </Stack>
            {mode === "Anonymous" && (
              <Alert severity="warning" icon={false} sx={{ mt: 2 }}>
                {t.publicWarning}
              </Alert>
            )}
          </Paper>
          {children}
        </Box>
      </Box>
    </Box>
  );
}
