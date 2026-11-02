import { useEffect, useState, type FormEvent } from "react";
import { request, type ModelSettings, type PackageFeed, type PackageFeedSettings } from "../api";
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

type FeedDraft = PackageFeed & { secret: string; clearCredential: boolean };

function PackageFeeds() {
  const current = useApi<PackageFeedSettings>("/api/settings/package-feeds");
  const [feeds, setFeeds] = useState<FeedDraft[]>([]);
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState<{ tone: "ok" | "bad"; text: string } | null>(null);

  useEffect(() => {
    if (current.data) setFeeds(current.data.feeds.map(feed => ({ ...feed, secret: "", clearCredential: false })));
  }, [current.data]);

  if (current.error) return <ErrorBox message={current.error} />;
  if (!current.data) return <Loading what="Loading package feeds" />;

  const update = (index: number, patch: Partial<FeedDraft>) => setFeeds(old => old.map((feed, i) => i === index ? { ...feed, ...patch } : feed));
  const add = (kind: PackageFeed["kind"]) => setFeeds(old => [...old, {
    id: crypto.randomUUID().replaceAll("-", ""), kind, name: "", url: "", scope: "", username: "", hasCredential: false, secret: "", clearCredential: false,
  }]);
  const save = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setStatus(null);
    try {
      await request<PackageFeedSettings>("/api/settings/package-feeds", {
        method: "PUT",
        body: JSON.stringify({ feeds: feeds.map(({ id, kind, name, url, scope, username, secret, clearCredential }) => ({ id, kind, name, url, scope, username, secret, clearCredential })) }),
      });
      setStatus({ tone: "ok", text: "Saved. The next analysis run will use these feeds." });
      current.reload();
    } catch (err) {
      setStatus({ tone: "bad", text: (err as Error).message });
    } finally {
      setBusy(false);
    }
  };

  return (
    <Card>
      <H2>Package feeds</H2>
      <Muted>
        NuGet sources are used for package metadata and reference assemblies. npm registries are used for lockfile-backed projects; install lifecycle scripts are disabled. Credentials are stored in <Mono className="text-xs">{current.data.settingsPath}</Mono>, never sent back to the browser, and only passed to package fetching.
      </Muted>
      {current.data.problem && <Note>Saved feeds could not be loaded: {current.data.problem}</Note>}
      <form onSubmit={save} className="mt-3 space-y-3">
        {feeds.length === 0 && <p className="text-[13px] text-zinc-500">No additional feeds configured.</p>}
        {feeds.map((feed, index) => (
          <div key={feed.id} className="grid gap-2 border-t border-zinc-200 pt-3 dark:border-zinc-800 sm:grid-cols-2">
            <label className="block">
              <span className="text-[13px] font-semibold">Type</span>
              <select value={feed.kind} onChange={e => update(index, { kind: e.target.value as PackageFeed["kind"], scope: "" })} className={`${inputClass} mt-1`}>
                <option value="nuget">NuGet</option>
                <option value="npm">npm</option>
              </select>
            </label>
            <label className="block">
              <span className="text-[13px] font-semibold">Name</span>
              <input value={feed.name} onChange={e => update(index, { name: e.target.value })} className={`${inputClass} mt-1`} autoComplete="off" />
            </label>
            <label className="block sm:col-span-2">
              <span className="text-[13px] font-semibold">{feed.kind === "nuget" ? "NuGet v3 service index" : "npm registry URL"}</span>
              <input value={feed.url} onChange={e => update(index, { url: e.target.value })} placeholder={feed.kind === "nuget" ? "https://packages.example.com/v3/index.json" : "https://packages.example.com/npm/"} className={`${inputClass} mt-1`} autoComplete="off" spellCheck={false} />
            </label>
            {feed.kind === "npm" ? (
              <label className="block sm:col-span-2">
                <span className="text-[13px] font-semibold">Scopes</span>
                <input value={feed.scope ?? ""} onChange={e => update(index, { scope: e.target.value })} placeholder="@company, @internal (blank sets the default registry)" className={`${inputClass} mt-1`} autoComplete="off" spellCheck={false} />
              </label>
            ) : (
              <label className="block">
                <span className="text-[13px] font-semibold">Username</span>
                <input value={feed.username ?? ""} onChange={e => update(index, { username: e.target.value })} className={`${inputClass} mt-1`} autoComplete="off" />
              </label>
            )}
            <label className="block">
              <span className="text-[13px] font-semibold">{feed.kind === "nuget" ? "Password or token" : "Token or password"}</span>
              <input type="password" value={feed.secret} onChange={e => update(index, { secret: e.target.value, clearCredential: false })} placeholder={feed.hasCredential ? "Saved; leave empty to keep" : "Optional for public feeds"} className={`${inputClass} mt-1`} autoComplete="new-password" />
            </label>
            <div className="flex flex-wrap items-center justify-between gap-2 sm:col-span-2">
              {feed.hasCredential && <label className="flex items-center gap-2 text-[13px]">
                <input type="checkbox" checked={feed.clearCredential} onChange={e => update(index, { clearCredential: e.target.checked, secret: "" })} />
                Clear saved credential
              </label>}
              <Button variant="danger" onClick={() => setFeeds(old => old.filter((_, i) => i !== index))}>Remove feed</Button>
            </div>
          </div>
        ))}
        <div className="flex flex-wrap gap-2">
          <Button variant="secondary" onClick={() => add("nuget")}>Add NuGet source</Button>
          <Button variant="secondary" onClick={() => add("npm")}>Add npm registry</Button>
          <Button type="submit" disabled={busy}>{busy ? "Saving…" : "Save feeds"}</Button>
          {status && <span role="status" className={`self-center text-[13px] ${status.tone === "ok" ? "text-emerald-700 dark:text-emerald-300" : "text-red-700 dark:text-red-300"}`}>{status.text}</span>}
        </div>
      </form>
    </Card>
  );
}
