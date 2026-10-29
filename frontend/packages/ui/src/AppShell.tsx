import { useRef, type ReactNode } from "react";
import {
  AppBar,
  Box,
  Button,
  Divider,
  Link,
  Stack,
  Toolbar,
  Typography,
} from "@mui/material";
import type { Labels } from "./preferences";
import { BrandMark } from "./BrandMark";
interface Props {
  children: ReactNode;
  t: Labels;
  displayName: string;
  navigation: NavigationGroup[];
  active: string;
  onNavigate: (id: string) => void;
  mode: string;
  authenticated: boolean;
  onSignOut: () => void;
}
export interface NavigationGroup {
  id: string;
  label: string;
  items: { id: string; label: string }[];
}
export function AppShell({
  children,
  t,
  displayName,
  navigation,
  active,
  onNavigate,
  mode,
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
        <Toolbar sx={{ gap: 2, flexWrap: "wrap", py: 1 }}>
          <BrandMark size={36} />
          <Typography component="span" variant="h6" sx={{ fontWeight: 800, letterSpacing: "-.04em", flex: 1, minWidth: 0, overflowWrap: "anywhere", lineHeight: 1.15 }}>
            {displayName || "WorkerDock"}
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
          <Stack component="nav" aria-label={t.tenant} direction="column" sx={{ gap: 2, mt: 1.5 }}>
            {navigation.map((group) => (
              <Box
                component="section"
                key={group.id}
                aria-labelledby={`navigation-${group.id}`}
                sx={{ minWidth: 0 }}
              >
                <Typography
                  id={`navigation-${group.id}`}
                  component="h2"
                  variant="caption"
                  color="text.secondary"
                  sx={{ display: "block", px: 0.25, mb: 0.75, fontWeight: 700, letterSpacing: ".08em", textTransform: "uppercase" }}
                >
                  {group.label}
                </Typography>
                <Stack
                  spacing={0.25}
                  sx={{ ml: 0.5, pl: 1, borderLeft: "2px solid", borderColor: "divider" }}
                >
                  {group.items.map((item) => {
                    const selected = active === item.id;
                    return (
                      <Button
                        key={item.id}
                        component="a"
                        href={"#/" + item.id}
                        aria-current={selected ? "page" : undefined}
                        onClick={(e) => {
                          e.preventDefault();
                          onNavigate(item.id);
                        }}
                        sx={{
                          width: "100%",
                          minHeight: 38,
                          justifyContent: "flex-start",
                          textAlign: "left",
                          whiteSpace: "normal",
                          borderRadius: 1.25,
                          px: 1.25,
                          py: 0.75,
                          color: selected ? "primary.dark" : "text.secondary",
                          bgcolor: selected ? "primary.light" : "transparent",
                          fontWeight: selected ? 700 : 500,
                          "&:hover": { bgcolor: selected ? "primary.light" : "action.hover" },
                        }}
                      >
                        {item.label}
                      </Button>
                    );
                  })}
                </Stack>
              </Box>
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
          {children}
        </Box>
      </Box>
    </Box>
  );
}
