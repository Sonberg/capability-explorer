import { request, type Meta } from "./api";
import { isDatabaseMeta, type DatabaseMeta } from "./database-api";
import { DatabaseExplorer } from "./views/DatabaseExplorer";
import { ErrorBox, Loading } from "./components/ui";
import { href, SelectionProvider, useApi, useRoute, useSelection } from "./state";
import { lazy, Suspense, useEffect, useState } from "react";
import { ChatPanel } from "./components/ChatPanel";
import { CommandPalette } from "./components/CommandPalette";
import { FocusProvider } from "./focus";
import { Artifacts } from "./views/Artifacts";
import { Glossary } from "./views/Glossary";
import { Calculations } from "./views/Calculations";
import { Capabilities } from "./views/Capabilities";
import { Changes } from "./views/Changes";
import { Evaluation } from "./views/Evaluation";
import { Explore } from "./views/Explore";
import { BindingPreview } from "./views/BindingPreview";
import { Integrations } from "./views/Integrations";
import { Settings } from "./views/Settings";
import { HealthStatus } from "./components/HealthStatus";

// Load the shared LikeC4 canvas only when Architecture is opened.
const ArchitectureView = lazy(() => import("./views/Architecture").then((m) => ({ default: m.ArchitectureView })));
const ProcessesDecisions = lazy(() => import("./views/ProcessesDecisions").then((m) => ({ default: m.ProcessesDecisions })));

const VIEWS = [
  ["explore", "Explore"],
  ["architecture", "Architecture"],
  ["integrations/exposed", "Integrations: Exposed"],
  ["integrations/consumed", "Integrations: Consumed"],
  ["binding-preview", "Binding review"],
  ["calculations", "Calculations"],
  ["glossary", "Glossary"],
  ["changes", "Changes"],
  ["capabilities", "Capabilities"],
  ["artifacts", "Artifacts"],
  ["processes-decisions", "Processes & decisions"],
  ["evaluation", "Evaluation"],
] as const;

export function App() {
  const meta = useApi<Meta | DatabaseMeta>("/api/meta");
  useLiveReload(isDatabaseMeta(meta.data) ? undefined : meta.data, meta.reload);
  if (meta.error) return <main className="p-5"><ErrorBox message={meta.error} /></main>;
  if (!meta.data) return <main className="p-5"><Loading /></main>;
  if (isDatabaseMeta(meta.data)) return <DatabaseExplorer meta={meta.data} reload={meta.reload} />;
  return (
    <SelectionProvider meta={meta.data} reloadMeta={meta.reload}>
      <FocusProvider>
        <Shell />
      </FocusProvider>
    </SelectionProvider>
  );
}

/** Refetches scoped views when inputs change or a server restart replaces the snapshot. */
function useLiveReload(meta: Meta | undefined, reload: () => void) {
  const generation = meta?.generation;
  const snapshotId = meta?.snapshotId;
  useEffect(() => {
    if (generation === undefined) return;
    const timer = window.setInterval(() => {
      if (document.hidden) return;
      request<{ generation: number; snapshotId: string }>("/api/generation")
        .then((g) => (g.generation !== generation || g.snapshotId !== snapshotId) && reload())
        .catch(() => undefined); // server restarting: keep showing what we have
    }, 3000);
    return () => window.clearInterval(timer);
  }, [generation, snapshotId, reload]);
}

function Shell() {
  const route = useRoute();
  const { scope } = useSelection();
  const view = route.path;
  const [palette, setPalette] = useState(false);
  const [chat, setChat] = useState(false);
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && e.key === "j") {
        e.preventDefault();
        setChat((c) => !c);
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);
  const mac = /Mac|iPhone|iPad/.test(navigator.platform);
  return (
    <>
      <CommandPalette open={palette} setOpen={setPalette} views={VIEWS} />
      <header className="sticky top-0 z-10 flex h-13 items-center gap-4 border-b border-zinc-200 bg-white px-3 sm:gap-6 sm:px-5 dark:border-zinc-800 dark:bg-zinc-900">
        <a href={href("explore")} className="flex shrink-0 items-center gap-2 font-bold text-zinc-900 hover:no-underline dark:text-zinc-100">
          <svg viewBox="0 0 24 24" aria-hidden="true" className="size-5.5 fill-blue-600 stroke-blue-600 stroke-[1.4] dark:fill-blue-400 dark:stroke-blue-400">
            <circle cx="6" cy="6" r="2.6" />
            <circle cx="18" cy="7" r="2.6" />
            <circle cx="12" cy="18" r="2.6" />
            <path d="M8.3 7l7.3.2M7.3 8.2l3.6 7.7M16.8 9.4l-3.6 6.5" fill="none" />
          </svg>
          <span className="hidden sm:inline">Capability Explorer</span>
        </a>
        <nav aria-label="Views" className="flex min-w-0 gap-0.5 overflow-x-auto">
          {VIEWS.map(([id, label]) => id === "integrations/consumed" ? null : id === "integrations/exposed" ? (
            <select
              key="integrations"
              aria-label="Integrations"
              value={view.startsWith("integrations") ? view : ""}
              onChange={(e) => { window.location.hash = href(e.target.value); }}
              className={`cursor-pointer rounded-md bg-transparent px-3 py-1.5 ${
                view.startsWith("integrations") ? "bg-blue-50 font-semibold text-blue-700 dark:bg-blue-950 dark:text-blue-300" : "text-zinc-500 hover:bg-zinc-100 dark:text-zinc-400 dark:hover:bg-zinc-800"
              }`}
            >
              <option value="" disabled>Integrations</option>
              <option value="integrations/exposed">Integrations: Exposed</option>
              <option value="integrations/consumed">Integrations: Consumed</option>
            </select>
          ) : (
            <a
              key={id}
              href={href(id)}
              aria-current={view === id ? "page" : undefined}
              className={`whitespace-nowrap rounded-md px-3 py-1.5 ${
                view === id ? "bg-blue-50 font-semibold text-blue-700 dark:bg-blue-950 dark:text-blue-300" : "text-zinc-500 hover:bg-zinc-100 hover:text-zinc-900 dark:text-zinc-400 dark:hover:bg-zinc-800 dark:hover:text-zinc-100"
              }`}
            >
              {label}
            </a>
          ))}
        </nav>
        <div className="ml-auto flex shrink-0 items-center gap-1">
          <button
            type="button"
            onClick={() => setPalette(true)}
            aria-label="Open command menu"
            className="flex items-center gap-2 rounded-md border border-zinc-200 px-2.5 py-1 text-[13px] text-zinc-500 hover:bg-zinc-100 dark:border-zinc-700 dark:text-zinc-400 dark:hover:bg-zinc-800"
          >
            <span className="hidden md:inline">Search</span>
            <kbd className="font-sans text-xs">{mac ? "⌘K" : "Ctrl K"}</kbd>
          </button>
          <HealthStatus />
          <button
            type="button"
            onClick={() => setChat((c) => !c)}
            aria-pressed={chat}
            className={`rounded-md border px-2.5 py-1 text-[13px] ${chat ? "border-blue-300 bg-blue-50 text-blue-700 dark:border-blue-800 dark:bg-blue-950 dark:text-blue-300" : "border-zinc-200 text-zinc-500 hover:bg-zinc-100 dark:border-zinc-700 dark:text-zinc-400 dark:hover:bg-zinc-800"}`}
          >
            Ask <kbd className="hidden font-sans text-xs md:inline">{mac ? "⌘J" : "Ctrl J"}</kbd>
          </button>
          <a
            href={href("settings")}
            aria-label="Settings"
            title="Settings"
            aria-current={view === "settings" ? "page" : undefined}
            className={`rounded-md p-1.5 ${view === "settings" ? "bg-blue-50 text-blue-700 dark:bg-blue-950 dark:text-blue-300" : "text-zinc-500 hover:bg-zinc-100 hover:text-zinc-900 dark:text-zinc-400 dark:hover:bg-zinc-800 dark:hover:text-zinc-100"}`}
          >
            <svg viewBox="0 0 24 24" aria-hidden="true" className="size-4.5 fill-none stroke-current stroke-[1.8]">
              <circle cx="12" cy="12" r="3" />
              <path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z" />
            </svg>
          </a>
        </div>
      </header>
      <ScopeBar />
      {chat && <ChatPanel onClose={() => setChat(false)} />}
      <main className={`min-w-0 p-3 sm:p-5 ${chat ? "sm:mr-[420px]" : "w-full"}`}>
        <Suspense fallback={<Loading what="model view" />}>
          <Routed path={view} params={route.params} scope={scope} mode="files" />
        </Suspense>
      </main>
    </>
  );
}

function Routed({ path, params, scope, mode }: { path: string; params: URLSearchParams; scope: string; mode: "files" | "database" }) {
  switch (path) {
    case "architecture":
      return (
        <Suspense fallback={<Loading what="Loading canvas" />}>
          <ArchitectureView params={params} />
        </Suspense>
      );
    case "integrations/exposed":
      return <Integrations direction="exposed" />;
    case "integrations/consumed":
      return <Integrations direction="consumed" />;
    case "binding-preview":
      return <BindingPreview />;
    case "integrations":
      window.location.replace("#/integrations/exposed");
      return null;
    case "calculations":
      return <Calculations params={params} />;
    case "glossary":
      return <Glossary params={params} />;
    case "changes":
      return <Changes params={params} />;
    case "capabilities":
      return <Capabilities />;
    case "artifacts":
      return <Artifacts />;
    case "processes-decisions":
      return <ProcessesDecisions key={scope} scope={scope} params={params} mode={mode} />;
    case "evaluation":
      return <Evaluation />;
    case "settings":
      return <Settings />;
    default:
      return <Explore params={params} />;
  }
}

/** Capability selector and the revision chosen for each repository in scope. */
function ScopeBar() {
  const { meta, capability, setCapability, view, setRevision } = useSelection();
  const current = meta.capabilities.find((c) => c.id === capability);
  return (
    <section aria-label="Scope" className="flex flex-wrap items-center gap-2 border-b border-zinc-200 bg-zinc-100/70 px-3 py-2 text-[13px] sm:px-5 dark:border-zinc-800 dark:bg-zinc-900/60">
      <label className="flex items-center gap-2">
        <span className="font-semibold">Capability</span>
        <select
          aria-label="Capability"
          value={capability}
          onChange={(e) => setCapability(e.target.value)}
          className="rounded-md border border-zinc-300 bg-white px-2 py-1 font-medium dark:border-zinc-700 dark:bg-zinc-900"
        >
          <option value="">All repositories</option>
          {meta.capabilities.map((c) => (
            <option key={c.id} value={c.id}>
              {c.name}
            </option>
          ))}
        </select>
      </label>
      <a href={href("capabilities")} className="text-xs text-blue-700 hover:underline dark:text-blue-300">
        manage
      </a>
      <span className="mx-1 hidden h-5 w-px bg-zinc-300 sm:block dark:bg-zinc-700" />
      {Object.keys(view)
        .sort()
        .map((repo) => (
          <label key={repo} className="inline-flex max-w-full flex-wrap items-center gap-1.5 rounded-full border border-zinc-200 bg-white py-0.5 pl-2.5 pr-1 dark:border-zinc-700 dark:bg-zinc-900">
            <span className="min-w-0 break-all">{repo}</span>
            <select aria-label={`Revision of ${repo}`} value={view[repo]} onChange={(e) => setRevision(repo, e.target.value)} className="max-w-full cursor-pointer bg-transparent px-1 py-0.5 font-medium">
              {meta.revisions
                .filter((r) => r.repositoryId === repo)
                .map((r) => (
                  <option key={r.commitSha} value={r.commitSha}>
                    {r.label}
                    {r.commitSha === meta.defaultView[repo] ? " (latest)" : ""}
                  </option>
                ))}
            </select>
          </label>
        ))}
      {current && current.repositories.filter((r) => !(r in view)).length > 0 && (
        <span className="text-xs text-amber-700 dark:text-amber-300" title="Repositories in this capability with no analysed revision loaded">
          not analysed: {current.repositories.filter((r) => !(r in view)).join(", ")}
        </span>
      )}
      <span className="w-full text-xs text-zinc-500 sm:ml-auto sm:w-auto">A chosen combination of code revisions — not a deployment record.</span>
    </section>
  );
}
