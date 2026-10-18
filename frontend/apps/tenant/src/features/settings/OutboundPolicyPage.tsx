import { useEffect, useState } from "react";
import { Alert, Button, Checkbox, FormControlLabel, MenuItem, Paper, Stack, TextField, Typography } from "@mui/material";
import { ApiError, mutate, read } from "../../api/client";
import type { Labels } from "../../../../../packages/ui/src/preferences";

interface Policy { name: string; hosts: string[]; ports: number[]; privateCidrs: string[]; allowHttp: boolean; version: string }
export function OutboundPolicyPage({ t }: { t: Labels }) {
  const [name, setName] = useState("tenant-jobs"); const [policy, setPolicy] = useState<Policy>(); const [etag, setEtag] = useState<string>();
  const [hosts, setHosts] = useState(""); const [ports, setPorts] = useState("443"); const [cidrs, setCidrs] = useState(""); const [allowHttp, setAllowHttp] = useState(false);
  const [error, setError] = useState(""); const [saved, setSaved] = useState(false); const [invalid, setInvalid] = useState(false);
  const hostValues = hosts.split(/\r?\n/).map(x => x.trim()).filter(Boolean);
  const portValues = ports.split(",").map(x => Number(x.trim()));
  const cidrValues = cidrs.split(/\r?\n/).map(x => x.trim()).filter(Boolean);
  const validHosts = hostValues.length <= 20 && hostValues.every(x => x.length <= 253 && /^(\*\.)?[A-Za-z0-9.-]+$/.test(x));
  const validPorts = portValues.length > 0 && portValues.length <= 32 && portValues.every(x => Number.isInteger(x) && x >= 1 && x <= 65535) && new Set(portValues).size === portValues.length;
  const validCidrs = cidrValues.length <= 16 && cidrValues.every(x => /^[0-9A-Fa-f:.]+\/\d{1,3}$/.test(x));
  const load = (key = name) => { setError(""); void read<Policy>(`/api/settings/egress/${key}`).then(({ data, etag: tag }) => { setPolicy(data); setEtag(tag ?? undefined); setHosts(data.hosts.join("\n")); setPorts(data.ports.join(",")); setCidrs(data.privateCidrs.join("\n")); setAllowHttp(data.allowHttp); }).catch(() => setError(t.requestError)); };
  useEffect(() => { load(name); }, [name]);
  const save = async () => {
    if (!policy || !etag) return;
    setInvalid(true); setSaved(false);
    if (!validHosts || !validPorts || !validCidrs) return;
    try { const next = await mutate<Policy>(`/api/settings/egress/${name}`, "PUT", { hosts: hostValues, ports: portValues, privateCidrs: cidrValues, allowHttp }, etag); setPolicy(next); setEtag('"' + next.version + '"'); setSaved(true); setError(""); setInvalid(false); }
    catch (cause) { setError(cause instanceof ApiError && cause.status === 412 ? t.stale : t.requestError); }
  };
  return <Paper variant="outlined" sx={{ p: 3, maxWidth: 720 }}><Stack spacing={2}>
    <Typography component="h2" variant="h6">{t.outboundSettings}</Typography>
    <Typography color="text.secondary">{t.outboundSettingsHelp}</Typography>
    <TextField select label={t.policyName} value={name} onChange={e => setName(e.target.value)}><MenuItem value="tenant-jobs">tenant-jobs</MenuItem><MenuItem value="tenant-alerts">tenant-alerts</MenuItem></TextField>
    {error && <Alert severity="error" action={<Button color="inherit" onClick={() => load()}>{t.reload}</Button>}>{error}</Alert>}
    {saved && <Alert severity="success">{t.saved}</Alert>}
    {policy && <>
      <TextField multiline minRows={2} label={t.allowedHosts} helperText={invalid && !validHosts ? t.validation : t.allowedHostsHelp} error={invalid && !validHosts} value={hosts} onChange={e => setHosts(e.target.value)} />
      <TextField label={t.allowedPorts} helperText={invalid && !validPorts ? t.validation : undefined} error={invalid && !validPorts} value={ports} onChange={e => setPorts(e.target.value)} />
      <TextField multiline minRows={2} label={t.privateCidrs} helperText={invalid && !validCidrs ? t.validation : t.privateCidrsHelp} error={invalid && !validCidrs} value={cidrs} onChange={e => setCidrs(e.target.value)} />
      <FormControlLabel control={<Checkbox checked={allowHttp} onChange={e => setAllowHttp(e.target.checked)} />} label={t.allowHttp} />
      <Typography variant="body2" color="text.secondary">{t.hardDeniedNotice}</Typography>
      <Button variant="contained" onClick={save} disabled={!etag}>{t.save}</Button>
    </>}
  </Stack></Paper>;
}
