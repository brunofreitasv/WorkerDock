import { useEffect, useState } from "react";
import { Alert, Box, Button, CircularProgress, MenuItem, Paper, Stack, TextField, Typography } from "@mui/material";
import { SecretReferenceField, type Language } from "@workerdock/ui";
import { ApiError, mutate, read } from "../../api/client";

type Profile = { profileRevisionId: string; profileId: string; revision: number; name: string; server: string; allowedCatalogs: string[]; authentication: string; userName: string | null; passwordReference: { name: string } | null; encrypt: boolean; trustServerCertificate: boolean };
type ProfileItem = { current: Profile; version: number[]; retired: boolean };
const zero = "00000000-0000-0000-0000-000000000000";
const empty = (): Profile => ({ profileRevisionId: zero, profileId: zero, revision: 0, name: "SQL Server profile", server: "", allowedCatalogs: [], authentication: "Integrated", userName: null, passwordReference: null, encrypt: true, trustServerCertificate: false });

export function ProfileEditor({ language = "en" }: { language?: Language }) {
  const groupCopy = language === "pt-BR"
    ? { connection: "Conexão", authentication: "Autenticação", actions: "Ações do perfil" }
    : { connection: "Connection", authentication: "Authentication", actions: "Profile actions" };
  const [items, setItems] = useState<ProfileItem[]>([]); const [selected, setSelected] = useState<ProfileItem>();
  const [profile, setProfile] = useState<Profile>(empty); const [etag, setEtag] = useState<string>(); const [catalog, setCatalog] = useState("");
  const [loading, setLoading] = useState(true); const [busy, setBusy] = useState(false); const [message, setMessage] = useState(""); const [diagnostic, setDiagnostic] = useState("");
  const load = async () => { setLoading(true); try { const { data } = await read<{ items: ProfileItem[] }>("/api/connection-profiles/"); setItems(data.items); } catch { setMessage("Unable to load connection profiles."); } finally { setLoading(false); } };
  useEffect(() => { void load(); }, []);
  const save = async () => { setBusy(true); setMessage(""); try {
    const body = { ...profile, allowedCatalogs: catalog.split(/\r?\n/).map((x) => x.trim()).filter(Boolean), passwordReference: profile.authentication === "VaultPassword" ? profile.passwordReference : null };
    if (selected) await mutate(`/api/connection-profiles/${selected.current.profileId}`, "PUT", body, etag);
    else await mutate("/api/connection-profiles/", "POST", body);
    setMessage("Profile saved."); setSelected(undefined); setProfile(empty()); setEtag(undefined); await load();
  } catch (error) { setMessage(error instanceof ApiError && error.status === 412 ? "Profile changed. Your edits are preserved; reload before saving." : "Unable to save profile."); } finally { setBusy(false); } };
  const edit = async (item: ProfileItem) => { try { const result = await read<Profile>(`/api/connection-profiles/${item.current.profileId}`); setSelected(item); setProfile(result.data); setEtag(result.etag ?? undefined); setCatalog(result.data.allowedCatalogs.join("\n")); setMessage(""); } catch { setMessage("Unable to load profile."); } };
  const runDiagnostic = async () => { if (!selected || !catalog.trim()) return; setBusy(true); setDiagnostic(""); try { const result = await mutate<{ commandId: string }>(`/api/connection-profiles/${selected.current.profileId}/diagnostics`, "POST", { catalog: catalog.split(/\r?\n/).map((x) => x.trim()).filter(Boolean)[0] }); const state = await read<{ state: string }>(`/api/commands/${result.commandId}`); setDiagnostic(`Diagnostic ${state.data.state.toLowerCase()}.`); } catch { setDiagnostic("Diagnostic could not be started. Check the profile and catalog settings."); } finally { setBusy(false); } };
  const retire = async () => { if (!selected || !etag) return; setBusy(true); try { await mutate(`/api/connection-profiles/${selected.current.profileId}/retire`, "POST", undefined, etag); setMessage("Profile retired."); setSelected(undefined); await load(); } catch (error) { setMessage(error instanceof ApiError && error.status === 412 ? "Profile changed. Your edits are preserved; reload before saving." : "Unable to retire profile."); } finally { setBusy(false); } };
  if (loading && !items.length) return <CircularProgress aria-label="Loading profiles" />;
  return <Stack spacing={3} sx={{ maxWidth: 900 }}>
    {message && <Alert severity={message.includes("preserved") || message.includes("Unable") ? "error" : "success"}>{message}</Alert>}
    <Paper component="section" aria-label="SQL Server profiles" variant="outlined" sx={{ p: { xs: 2, sm: 3 } }}>
      <Stack spacing={2}>
        <Typography component="h2" variant="h6">SQL Server profiles</Typography>
        {items.length === 0 && <Box sx={{ py: 2 }}><Typography sx={{ fontWeight: 600 }}>{language === "pt-BR" ? "Nenhum perfil do SQL Server ainda." : "No SQL Server profiles yet."}</Typography><Typography color="text.secondary">{language === "pt-BR" ? "Crie um perfil para reutilizar uma conexão nas tarefas." : "Create a profile to reuse a connection in jobs."}</Typography></Box>}
        {items.map((item) => <Stack key={item.current.profileId} direction={{ xs: "column", sm: "row" }} spacing={1} sx={{ alignItems: { sm: "center" }, py: 1, borderBottom: 1, borderColor: "divider" }}><Button onClick={() => void edit(item)} sx={{ justifyContent: "flex-start" }}>{item.current.name}</Button><Typography color="text.secondary" sx={{ overflowWrap: "anywhere" }}>{item.current.server} · {item.current.profileId} {item.retired ? "· retired" : ""}</Typography></Stack>)}
        <Button onClick={() => { setSelected(undefined); setProfile(empty()); setCatalog(""); setEtag(undefined); }} sx={{ alignSelf: { xs: "stretch", sm: "flex-start" } }}>New profile</Button>
      </Stack>
    </Paper>
    <Paper component="section" aria-label={selected ? "Edit profile" : "New SQL Server profile"} variant="outlined" sx={{ p: { xs: 2, sm: 3 } }}>
      <Stack spacing={3}>
        <Typography component="h2" variant="h6">{selected ? "Edit profile" : "New SQL Server profile"}</Typography>
        <Box role="group" aria-label={groupCopy.connection} sx={{ display: "grid", gridTemplateColumns: { xs: "1fr", sm: "repeat(2, minmax(0, 1fr))" }, gap: 2 }}>
          <TextField label="Profile name" value={profile.name} onChange={(e) => setProfile({ ...profile, name: e.target.value })} required />
          <TextField label="Server" value={profile.server} onChange={(e) => setProfile({ ...profile, server: e.target.value })} required />
          <TextField label="Allowed catalogs" multiline minRows={2} helperText="One catalog name per line." value={catalog} onChange={(e) => setCatalog(e.target.value)} required sx={{ gridColumn: { sm: "1 / -1" } }} />
        </Box>
        <Box role="group" aria-label={groupCopy.authentication} sx={{ display: "grid", gridTemplateColumns: { xs: "1fr", sm: "repeat(2, minmax(0, 1fr))" }, gap: 2 }}>
          <TextField select label="Authentication" value={profile.authentication} onChange={(e) => setProfile({ ...profile, authentication: e.target.value })}><MenuItem value="Integrated">Integrated</MenuItem><MenuItem value="VaultPassword">Vault password</MenuItem></TextField>
          {profile.authentication === "VaultPassword" && <><TextField label="User name" value={profile.userName ?? ""} onChange={(e) => setProfile({ ...profile, userName: e.target.value })} /><SecretReferenceField value={profile.passwordReference?.name ?? ""} onChange={(name) => setProfile({ ...profile, passwordReference: name ? { name } : null })} /></>}
          <TextField select label="Encrypt connection" value={String(profile.encrypt)} onChange={(e) => setProfile({ ...profile, encrypt: e.target.value === "true" })}><MenuItem value="true">Enabled</MenuItem><MenuItem value="false">Disabled</MenuItem></TextField>
          <TextField select label="Trust server certificate" value={String(profile.trustServerCertificate)} onChange={(e) => setProfile({ ...profile, trustServerCertificate: e.target.value === "true" })}><MenuItem value="false">Disabled</MenuItem><MenuItem value="true">Enabled</MenuItem></TextField>
        </Box>
        <Stack role="group" aria-label={groupCopy.actions} direction={{ xs: "column", sm: "row" }} spacing={1}>
          <Button variant="contained" disabled={busy || selected?.retired} onClick={() => void save()}>Save profile</Button>
          {selected && !selected.retired && <Button disabled={busy || !catalog.trim()} onClick={() => void runDiagnostic()}>Run safe diagnostic</Button>}
          {selected && !selected.retired && <Button disabled={busy} onClick={() => void retire()}>Retire profile</Button>}
        </Stack>
        {diagnostic && <Alert severity={diagnostic.startsWith("Diagnostic") ? "info" : "error"} role="status">{diagnostic}</Alert>}
        <Typography variant="caption" color="text.secondary">Manager-only profiles. Passwords are stored as secret references and are never shown or accepted here.</Typography>
      </Stack>
    </Paper>
  </Stack>;
}
