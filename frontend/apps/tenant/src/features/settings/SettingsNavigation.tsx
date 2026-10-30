import { Box, Tab, Tabs } from "@mui/material";
import type { Labels } from "@workerdock/ui";

export type SettingsTab = "personal" | "general" | "vault" | "outbound" | "profiles" | "retention";

export function SettingsNavigation({ t, selected, canManage, onSelect }: {
  t: Labels;
  selected: SettingsTab;
  canManage: boolean;
  onSelect: (tab: SettingsTab) => void;
}) {
  return <Box sx={{ borderBottom: 1, borderColor: "divider", mb: 3, maxWidth: "100%" }}>
    <Tabs value={canManage ? selected : "personal"} onChange={(_event, value: SettingsTab) => onSelect(value)} variant="scrollable" selectionFollowsFocus scrollButtons="auto" allowScrollButtonsMobile aria-label={t.settings}>
      <Tab value="personal" label={t.personalSettings} />
      {canManage && [
        ["general", t.tenantConfiguration],
        ["vault", t.vaultSettings],
        ["outbound", t.outboundSettings],
        ["profiles", t.profiles],
        ["retention", t.retention],
      ].map(([value, label]) => <Tab key={value} value={value} label={label} />)}
    </Tabs>
  </Box>;
}
