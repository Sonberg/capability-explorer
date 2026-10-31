import { useState, type FormEvent } from "react";
import { ApiError, request, shortSha, type CapabilityStatus } from "../api";
import { Button, Card, ErrorBox, H1, H2, inputClass, Loading, Mono, Muted, Note, Pill, Table } from "../components/ui";
import { useApi, useSelection } from "../state";

interface LineError {
  line: number;
  input: string;
  message: string;
}

export function Capabilities() {
  const { meta, reloadMeta, setCapability, capability } = useSelection();
  const list = useApi<CapabilityStatus[]>(`/api/capabilities?g=${meta.generation}`);
  const [busy, setBusy] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string>();

  const refresh = () => {
    list.reload();
    reloadMeta();
  };

  const check = async (id: string) => {
    setBusy(id);
    setActionError(undefined);
    try {
      await request(`/api/capabilities/${encodeURIComponent(id)}/check`, { method: "POST" });
      refresh();
    } catch (e) {
      setActionError((e as Error).message);
    } finally {
      setBusy(null);
    }
  };

  const remove = async (c: CapabilityStatus) => {
    if (!window.confirm(`Remove capability “${c.name}”? Cloned sources stay in the workspace.`)) return;
    setActionError(undefined);
    try {
      await request(`/api/capabilities/${encodeURIComponent(c.id)}`, { method: "DELETE" });
      if (capability === c.id) setCapability("");
      refresh();
    } catch (e) {
      setActionError((e as Error).message);
    }
  };

  return (
    <div className="space-y-3">
      <div>
        <H1>Capabilities</H1>
        <Muted>
          A capability is a named set of repositories that are checked together. Checking acquires each source read-only (GitHub repositories as bare, blob-less clones; local repositories in place) and
          locks it to one exact commit. Pick a capability in the bar above to scope every view to it.
        </Muted>
      </div>
      <div className="flex flex-wrap items-center gap-2 text-xs">
        <Pill tone={meta.githubAuth.configured ? "ok" : "neutral"}>{meta.githubAuth.configured ? "GitHub credentials available" : "no GitHub credentials"}</Pill>
        <span className="text-zinc-500">
          {meta.githubAuth.configured
            ? `Private repositories are cloned with the token from ${meta.githubAuth.source}, sent only to github.com.`
            : "Public repositories only. For private ones, start the explorer with GITHUB_TOKEN set or after `gh auth login` on your machine."}
        </span>
      </div>
      {actionError && <ErrorBox message={actionError} />}
      {list.error && <ErrorBox message={list.error} />}
      {!list.data && !list.error && <Loading />}
      {list.data?.length === 0 && <Card className="text-zinc-500">No capabilities yet. Define one below.</Card>}
      {list.data?.map((c) => (
        <Card key={c.id}>
          <div className="flex flex-wrap items-center gap-2">
            <H2 className="mb-0">{c.name}</H2>
            <Mono className="text-xs text-zinc-500">{c.id}</Mono>
            <Pill tone={c.analysed === c.repositories.length ? "ok" : c.analysed > 0 ? "warn" : "neutral"}>
              {c.analysed}/{c.repositories.length} analysed at locked revision
            </Pill>
            <span className="ml-auto flex gap-2">
              <Button variant="secondary" onClick={() => setCapability(c.id)} disabled={capability === c.id}>
                {capability === c.id ? "Selected" : "Select"}
              </Button>
              <Button onClick={() => check(c.id)} disabled={busy !== null}>
                {busy === c.id ? "Checking…" : "Check now"}
              </Button>
              <Button variant="danger" onClick={() => remove(c)}>
                Remove
              </Button>
            </span>
          </div>
          {c.description && <Muted className="mt-1">{c.description}</Muted>}
          <p className="my-2 text-xs text-zinc-500">{c.checkedAt ? `Last checked ${new Date(c.checkedAt).toLocaleString()}` : "Not checked yet."}</p>
          {c.analysed < c.repositories.length && <AnalyseHint id={c.id} />}
          <Table
            columns={["Repository", "Source", "Ref", "Locked revision", "Analysis"]}
            rows={c.repositories.map((r) => [
              <span className="font-semibold">{r.id}</span>,
              <span className="inline-flex flex-wrap items-center gap-1.5">
                <Pill tone={r.kind === "github" ? "info" : "neutral"}>{r.kind}</Pill>
                {r.kind === "github" ? (
                  <a href={r.location} target="_blank" rel="noreferrer noopener" className="break-all font-mono text-xs text-blue-700 hover:underline dark:text-blue-300">
                    {r.location}
                  </a>
                ) : (
                  <Mono className="break-all text-xs">{r.location}</Mono>
                )}
              </span>,
              <Mono className="text-xs">{r.ref}</Mono>,
              r.error ? (
                <span className="text-xs text-red-700 dark:text-red-300">{r.error}</span>
              ) : r.lockedSha ? (
                <span className="text-xs">
                  <Mono>{shortSha(r.lockedSha)}</Mono> <span className="text-zinc-500">{r.lockedSubject}</span>
                </span>
              ) : (
                <span className="text-xs text-zinc-500">not checked</span>
              ),
              r.analysed ? (
                <Pill tone="ok">analysed</Pill>
              ) : (
                <span className="text-xs">
                  <Pill tone="warn">not analysed</Pill>
                  <span className="block text-zinc-500">{r.analysedRevisions.length ? `other revisions: ${r.analysedRevisions.join(", ")}` : "no artifacts for this repository yet"}</span>
                </span>
              ),
            ])}
          />
        </Card>
      ))}
      <NewCapability onCreated={(id) => { refresh(); setCapability(id); }} />
    </div>
  );
}

/** Extraction runs outside the explorer, in offline sandboxes; the page refreshes by itself when it finishes. */
function AnalyseHint({ id }: { id: string }) {
  const command = `make analyze CAPABILITY=${id}`;
  return (
    <Note>
      <span className="block">
        To analyse the locked revisions, run in the repository root: <Mono className="select-all">{command}</Mono>
        <button type="button" className="ml-2 text-blue-700 hover:underline dark:text-blue-300" onClick={() => void navigator.clipboard?.writeText(command)}>
          copy
        </button>
      </span>
      <span className="block text-zinc-500">
        It checks each repository again (add LOCKED=1 to keep these revisions), extracts C# and TypeScript without building or running anything, and this page picks up the results automatically.
      </span>
    </Note>
  );
}

function NewCapability({ onCreated }: { onCreated: (id: string) => void }) {
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [repositories, setRepositories] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [lineErrors, setLineErrors] = useState<LineError[]>([]);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(undefined);
    setLineErrors([]);
    try {
      const created = await request<CapabilityStatus>("/api/capabilities", { method: "POST", body: JSON.stringify({ name, description, repositories }) });
      setName("");
      setDescription("");
      setRepositories("");
      onCreated(created.id);
    } catch (err) {
      setError((err as Error).message);
      if (err instanceof ApiError) setLineErrors(((err.body as { lines?: LineError[] } | undefined)?.lines ?? []) as LineError[]);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Card>
      <H2>New capability</H2>
      <form onSubmit={submit} className="grid gap-3">
        <label className="grid gap-1">
          <span className="text-xs font-semibold">Name</span>
          <input required value={name} onChange={(e) => setName(e.target.value)} placeholder="Order to cash" className={inputClass} />
        </label>
        <label className="grid gap-1">
          <span className="text-xs font-semibold">Description (optional)</span>
          <input value={description} onChange={(e) => setDescription(e.target.value)} className={inputClass} />
        </label>
        <label className="grid gap-1">
          <span className="text-xs font-semibold">Repositories — one per line</span>
          <textarea
            required
            rows={6}
            value={repositories}
            onChange={(e) => setRepositories(e.target.value)}
            placeholder={"https://github.com/acme/orders-api\nhttps://github.com/acme/web/tree/release/2026.10\nbilling=https://github.com/acme/billing-service v4.2.0\n/home/me/code/warehouse-jobs"}
            className={`${inputClass} font-mono`}
          />
          <span className="text-xs text-zinc-500">
            <Mono>[id=]location [ref]</Mono> — location is <Mono>https://github.com/owner/repo</Mono> (optionally <Mono>/tree/branch</Mono>) or a local git repository. Ref defaults to HEAD. Local paths
            outside this checkout must be mounted: <Mono>make explore SOURCES=/abs/path,…</Mono>. Private GitHub repositories need credentials available to git in the sandbox.
          </span>
        </label>
        {error && <Note>{error}</Note>}
        {lineErrors.length > 0 && (
          <ul className="list-disc pl-5 text-xs text-red-700 dark:text-red-300">
            {lineErrors.map((l) => (
              <li key={l.line}>
                line {l.line}: <Mono>{l.input}</Mono> — {l.message}
              </li>
            ))}
          </ul>
        )}
        <div>
          <Button type="submit" disabled={busy}>
            {busy ? "Creating and checking…" : "Create and check"}
          </Button>
        </div>
      </form>
    </Card>
  );
}
