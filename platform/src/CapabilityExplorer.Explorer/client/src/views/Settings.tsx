import { useEffect, useState, type FormEvent } from "react";
import { request, type ModelSettings, type PackageFeedSettings } from "../api";
import { Button, Card, ErrorBox, H1, H2, inputClass, Loading, Mono, Muted, Note, Pill } from "../components/ui";
import { useApi, useSelection } from "../state";

/** Model connection (local Ollama by default, or Foundry) for explanations and similarity search. The API key is write-only. */
export function Settings() {
  const { reloadMeta } = useSelection();
  const current = useApi<ModelSettings>("/api/settings/model");
  const [form, setForm] = useState({ endpoint: "", chatModel: "", embeddingModel: "", apiKey: "", clearApiKey: false });
  const [status, setStatus] = useState<{ tone: "ok" | "bad"; text: string } | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    const d = current.data;
    if (d) setForm({ endpoint: d.endpoint ?? "", chatModel: d.chatModel ?? "", embeddingModel: d.embeddingModel ?? "", apiKey: "", clearApiKey: false });
  }, [current.data]);

  if (current.error) return <ErrorBox message={current.error} />;
  if (!current.data) return <Loading />;
  const d = current.data;

  const save = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setStatus(null);
    try {
      await request<ModelSettings>("/api/settings/model", { method: "PUT", body: JSON.stringify(form) });
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
      const r = await request<{ model: string; dimensions: number }>("/api/settings/model/test", { method: "POST" });
      setStatus({ tone: "ok", text: `Connected: ${r.model} returned a ${r.dimensions}-dimension vector.` });
    } catch (err) {
      setStatus({ tone: "bad", text: (err as Error).message });
    } finally {
      setBusy(false);
    }
  };

  const field = (name: "endpoint" | "chatModel" | "embeddingModel", label: string, placeholder: string, hint: string) => (
    <label className="block">
      <span className="text-[13px] font-semibold">{label}</span>
      <input name={name} value={form[name]} onChange={(e) => setForm({ ...form, [name]: e.target.value })} placeholder={placeholder} className={`${inputClass} mt-1`} autoComplete="off" spellCheck={false} />
      <span className="mt-0.5 block text-xs text-zinc-500">{hint}</span>
    </label>
  );

  return (
    <div className="mx-auto max-w-2xl space-y-3">
      <Card>
        <H1>Models</H1>
        <Muted>
          Used for “Explain differences” (chat model) and for similarity search (embedding model). Everything else works without them. A local Ollama needs no key and keeps the code on this machine:
          run <Mono>make ollama</Mono>, or fill in the local defaults below. Any OpenAI-compatible API works, including Microsoft Foundry.
        </Muted>
        <div className="mt-2 flex flex-wrap gap-1.5">
          <Pill tone={d.source === "none" ? "neutral" : "ok"}>{d.source === "none" ? "not configured" : `${d.provider}${d.source === "configuration" ? " (command line)" : ""}`}</Pill>
          <Pill tone={d.database ? "ok" : "warn"} title="Similarity search reads vectors from PostgreSQL">
            {d.database ? "database connected" : "no database (CE_DATABASE)"}
          </Pill>
        </div>
        {d.problem && <Note>Saved settings were ignored at startup: {d.problem}</Note>}
        {d.source === "configuration" && <Note>The command line configures a provider; saving here replaces it until the explorer restarts.</Note>}

        <form onSubmit={save} className="mt-4 space-y-3">
          <div>
            <Button variant="secondary" onClick={() => setForm({ ...form, endpoint: d.ollamaEndpoint, chatModel: "qwen2.5:7b", embeddingModel: "nomic-embed-text", apiKey: "", clearApiKey: d.apiKeySource === "saved" })}>
              Use local Ollama
            </Button>
          </div>
          {field("endpoint", "Endpoint", d.ollamaEndpoint, "API base: Ollama is …:11434/v1; a Foundry resource can be given as https://<name>.services.ai.azure.com. Leave empty to turn models off.")}
          {field("chatModel", "Chat model", "qwen2.5:7b", "Explains differences between calculations. Optional.")}
          {field("embeddingModel", "Embedding model", "nomic-embed-text", "Builds vectors for similarity search and the Cmd+K “Meaning” results. Changing it means embedding again. Optional.")}
          <label className="block">
            <span className="text-[13px] font-semibold">API key</span>
            <input
              name="apiKey"
              type="password"
              value={form.apiKey}
              onChange={(e) => setForm({ ...form, apiKey: e.target.value })}
              placeholder={d.hasApiKey ? `set (${d.apiKeySource}) — leave empty to keep` : "not needed for Ollama; for Foundry, empty means Entra ID (az login)"}
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
            <Button variant="secondary" onClick={test} disabled={busy || !d.embeddingModel}>
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
      <PackageFeeds />
      <Card>
        <H2>Building the vectors</H2>
        <Muted>
          Embeddings are computed from the database, after an import. Run <Mono>make analyze CAPABILITY=&lt;id&gt;</Mono> with <Mono>CE_DATABASE</Mono> set, or <Mono>ce embed</Mono> on its own. Both read these saved settings. Unchanged symbols are not embedded again.
          Ollama runs on the host at port 11434; the explorer reaches it as <Mono>host.docker.internal</Mono>.
        </Muted>
      </Card>
    </div>
  );
}

function PackageFeeds() {
  const current = useApi<PackageFeedSettings>("/api/settings/package-feeds");
  if (current.error) return <ErrorBox message={current.error} />;
  if (!current.data) return <Loading what="Loading package feeds" />;
  return (
    <Card>
      <H2>Package feeds</H2>
      <Muted>
        Read-only host configuration from <Mono className="text-xs">{current.data.settingsPath}</Mono>.
        This public catalogue contains no credentials and does not indicate that a worker is running.
        Explorer cannot accept or save package-feed passwords or tokens.
      </Muted>
      <Note>
        Configure or migrate on the host: <Mono>ce package-feeds --import &lt;old-settings.json&gt; --catalog .sandbox-cache/package-feed-catalog/catalog.json</Mono>.
        Legacy secret files are not read by Explorer. Model and database settings are separate.
      </Note>
      {current.data.problem && <ErrorBox message={current.data.problem} />}
      {!current.data.problem && current.data.feeds.length === 0 && <Muted>No additional feeds configured.</Muted>}
      {current.data.feeds.map(feed => (
        <div key={feed.id} className="mt-3 border-t border-zinc-200 pt-2 dark:border-zinc-800">
          <strong>{feed.name}</strong> · {feed.kind} · <Mono>{feed.id}</Mono>
          <p className="break-all text-xs">{feed.url}</p>
          {feed.scope && <Muted>Scopes: {feed.scope}</Muted>}
          {feed.username && <Muted>Username: {feed.username}</Muted>}
          <Pill tone={feed.hasCredential ? "ok" : "neutral"}>{feed.hasCredential ? "Host credential configured" : "No host credential configured"}</Pill>
        </div>
      ))}
      <Button variant="secondary" onClick={current.reload}>Refresh catalogue</Button>
    </Card>
  );
}
