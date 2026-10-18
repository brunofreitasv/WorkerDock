import { useEffect, useState } from "react";
import { Alert, CircularProgress, MenuItem, TextField } from "@mui/material";
import { read } from "../../api/client";
import type { TemplateDescriptorDto } from "@workerdock/ui";

export type CatalogItem = { descriptor: TemplateDescriptorDto; selectable: boolean };
export function TemplatePicker({ value, onChange, label = "Template" }: { value?: CatalogItem; onChange(item: CatalogItem): void; label?: string }) {
  const [items, setItems] = useState<CatalogItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [failed, setFailed] = useState(false);
  useEffect(() => { let active = true; void read<{ items: CatalogItem[] }>("/api/templates").then(({ data }) => { if (active) setItems(data.items); }).catch(() => { if (active) setFailed(true); }).finally(() => { if (active) setLoading(false); }); return () => { active = false; }; }, []);
  if (loading) return <CircularProgress aria-label="Loading templates" size={24} />;
  if (failed) return <Alert severity="error">Unable to load templates.</Alert>;
  const key = value ? `${value.descriptor.id}@${value.descriptor.version}` : "";
  return <TextField select fullWidth label={label} value={key} onChange={(event) => { const selected = items.find((item) => `${item.descriptor.id}@${item.descriptor.version}` === event.target.value); if (selected) onChange(selected); }}>
    {items.map((item) => <MenuItem key={`${item.descriptor.id}@${item.descriptor.version}`} value={`${item.descriptor.id}@${item.descriptor.version}`} disabled={!item.selectable}>{item.descriptor.displayName} · {item.descriptor.version}{!item.selectable ? " · retired" : ""}</MenuItem>)}
  </TextField>;
}
