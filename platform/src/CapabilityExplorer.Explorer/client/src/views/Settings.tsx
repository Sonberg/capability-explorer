import { useEffect, useState, type FormEvent } from "react";
import { request, type FoundrySettings } from "../api";
import { Button, Card, ErrorBox, H1, H2, inputClass, Loading, Mono, Muted, Note, Pill } from "../components/ui";
import { useApi, useSelection } from "../state";

/** Foundry connection for explanations and similarity search. Saved on the server; the API key is write-only. */
export function Settings() {
  const { reloadMeta } = useSelection();
  const current = useApi<FoundrySettings>("/api/settings/foundry");
  const [form, setForm] = useState({ endpoint: "", deployment: "", embeddingDeployment: "", apiKey: "", clearApiKey: false });
  const [status, setStatus] = useState<{ tone: "ok" | "bad"; text: string } | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    const d = current.data;
    if (d) setForm({ endpoint: d.endpoint ?? "", deployment: d.deployment ?? "", embeddingDeployment: d.embeddingDeployment ?? "", apiKey: "", clearApiKey: false });
  }, [current.data]);

  if (current.error) return <ErrorBox message={current.error} />;
  if (!current.data) return <Loading />;
  const d = current.data;

  const save = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setStatus(null);
    try {
      await request<FoundrySettings>("/api/settings/foundry", { method: "PUT", body: JSON.stringify(form) });
      setStatus({ tone: "ok", text: "Saved and applied." });
      current.reload();
      reloadMeta();
    } catch (err) {
      setStatus({ tone: "bad", text: (err as Error).message });
    } finally {
      setBusy(false);
    }
  };

  const test = async () => {
    setBusy(true);
    setStatus(null);
    try {
      const r = await request<{ model: string; dimensions: number }>("/api/settings/foundry/test", { method: "POST" });
      setStatus({ tone: "ok", text: `Connected: ${r.model} returned a ${r.dimensions}-dimension vector.` });
    } catch (err) {
      setStatus({ tone: "bad", text: (err as Error).message });
    } finally {
      setBusy(false);
    }
  };

  const field = (name: "endpoint" | "deployment" | "embeddingDeployment", label: string, placeholder: string, hint: string) => (
    <label className="block">
      <span className="text-[13px] font-semibold">{label}</span>
      <input name={name} value={form[name]} onChange={(e) => setForm({ ...form, [name]: e.target.value })} placeholder={placeholder} className={`${inputClass} mt-1`} autoComplete="off" spellCheck={false} />
      <span className="mt-0.5 block text-xs text-zinc-500">{hint}</span>
    </label>
  );

  return (
    <div className="mx-auto max-w-2xl space-y-3">
      <Card>
        <H1>Microsoft Foundry</H1>
        <Muted>Used for “Explain differences” (chat deployment) and for similarity search (embedding deployment). Everything else works without it.</Muted>
        <div className="mt-2 flex flex-wrap gap-1.5">
          <Pill tone={d.source === "none" ? "neutral" : "ok"}>{d.source === "saved" ? "saved settings" : d.source === "configuration" ? "from command line" : "not configured"}</Pill>
          <Pill tone={d.database ? "ok" : "warn"} title="Similarity search reads vectors from PostgreSQL">
            {d.database ? "database connected" : "no database (CE_DATABASE)"}
          </Pill>
        </div>
        {d.problem && <Note>Saved settings were ignored at startup: {d.problem}</Note>}
        {d.source === "configuration" && <Note>The command line configures a provider; saving here replaces it until the explorer restarts.</Note>}

        <form onSubmit={save} className="mt-4 space-y-3">
          {field("endpoint", "Endpoint", "https://my-resource.services.ai.azure.com", "The Foundry resource endpoint. Leave empty to turn the integration off.")}
          {field("deployment", "Chat deployment", "gpt-4.1-mini", "Explains differences between calculations. Optional.")}
          {field("embeddingDeployment", "Embedding deployment", "text-embedding-3-small", "Builds vectors for similarity search and the Cmd+K “Meaning” results. Optional.")}
          <label className="block">
            <span className="text-[13px] font-semibold">API key</span>
            <input
              name="apiKey"
              type="password"
              value={form.apiKey}
              onChange={(e) => setForm({ ...form, apiKey: e.target.value })}
              placeholder={d.hasApiKey ? `set (${d.apiKeySource}) — leave empty to keep` : "empty: sign in with Entra ID (az login)"}
              className={`${inputClass} mt-1`}
              autoComplete="off"
              disabled={form.clearApiKey}
            />
            <span className="mt-0.5 block text-xs text-zinc-500">
              Stored in <Mono className="text-xs">{d.settingsPath}</Mono> (git-ignored, owner-only). It is never sent back to the browser.
            </span>
          </label>
          {d.apiKeySource === "saved" && (
            <label className="flex items-center gap-2 text-[13px]">
              <input type="checkbox" checked={form.clearApiKey} onChange={(e) => setForm({ ...form, clearApiKey: e.target.checked, apiKey: "" })} />
              Remove the saved key
            </label>
          )}
          <div className="flex flex-wrap items-center gap-2 pt-1">
            <Button type="submit" disabled={busy}>
              Save
            </Button>
            <Button variant="secondary" onClick={test} disabled={busy || !d.embeddingDeployment}>
              Test embeddings
            </Button>
            {status && (
              <span role="status" className={`text-[13px] ${status.tone === "ok" ? "text-emerald-700 dark:text-emerald-300" : "text-red-700 dark:text-red-300"}`}>
                {status.text}
              </span>
            )}
          </div>
        </form>
      </Card>
      <Card>
        <H2>Building the vectors</H2>
        <Muted>
          Embeddings are computed from the database, after an import. Run <Mono>make analyze CAPABILITY=&lt;id&gt;</Mono> with <Mono>CE_DATABASE</Mono> set, or <Mono>ce embed</Mono> on its own. Both read these saved settings. Unchanged symbols are not embedded again.
        </Muted>
      </Card>
    </div>
  );
}
