import { useEffect, useState } from "react";

type HealthResponse = {
  status: "healthy" | "degraded" | "unhealthy";
  uptime_seconds: number;
  version: string;
  timestamp: string;
};

type HealthState = {
  status: HealthResponse["status"] | "checking" | "unavailable";
  details?: HealthResponse;
  error?: string;
};

function isHealthResponse(value: unknown): value is HealthResponse {
  return typeof value === "object" && value !== null &&
    "status" in value && (value.status === "healthy" || value.status === "degraded" || value.status === "unhealthy") &&
    "uptime_seconds" in value && typeof value.uptime_seconds === "number" &&
    Number.isInteger(value.uptime_seconds) && value.uptime_seconds >= 0 &&
    "version" in value && typeof value.version === "string" && value.version.length > 0 && value.version.length <= 64 &&
    "timestamp" in value && typeof value.timestamp === "string" && value.timestamp.length <= 64 &&
    Number.isFinite(Date.parse(value.timestamp));
}

export function HealthStatus() {
  const [health, setHealth] = useState<HealthState>({ status: "checking" });
  const [preview, setPreview] = useState(false);
  const [pinned, setPinned] = useState(false);
  const open = preview || pinned;

  useEffect(() => {
    let active = true;
    let pending: AbortController | undefined;
    const check = async () => {
      if (pending) return;
      const controller = new AbortController();
      pending = controller;
      try {
        const response = await fetch("/health", {
          cache: "no-store", headers: { accept: "application/json" },
          signal: AbortSignal.any([controller.signal, AbortSignal.timeout(5_000)]),
        });
        if (response.status !== 200 && response.status !== 503) throw new Error(`Health endpoint returned HTTP ${response.status}`);
        const body: unknown = await response.json();
        if (!isHealthResponse(body)) throw new Error("Invalid health response");
        if (active) setHealth({ status: body.status, details: body });
      } catch (error) {
        if (active) setHealth({ status: "unavailable", error: error instanceof Error ? error.message : "Health request failed" });
      } finally {
        pending = undefined;
      }
    };

    void check();
    const timer = window.setInterval(check, 10_000);
    return () => {
      active = false;
      window.clearInterval(timer);
      pending?.abort();
    };
  }, []);

  const label = health.status === "checking" ? "Checking health"
    : health.status === "unavailable" ? "Unavailable"
    : `${health.status[0].toUpperCase()}${health.status.slice(1)}`;
  const tone = health.status === "healthy" ? "bg-emerald-500"
    : health.status === "degraded" ? "bg-amber-500"
    : health.status === "checking" ? "bg-zinc-400"
    : "bg-red-500";

  return (
    <div className="relative shrink-0" onMouseEnter={() => setPreview(true)} onMouseLeave={() => setPreview(false)}
      onKeyDown={(event) => {
        if (event.key === "Escape") { setPinned(false); setPreview(false); }
      }}>
      <button type="button" aria-label="Service health details" aria-expanded={open} aria-controls="service-health-details"
        className="rounded-md border border-zinc-200 px-2 py-1 text-xs text-zinc-600 hover:bg-zinc-100 focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue-600 dark:border-zinc-700 dark:text-zinc-300 dark:hover:bg-zinc-800"
        onClick={() => { setPinned(current => !current); setPreview(false); }}>
        <span className="inline-flex items-center gap-1.5" role="status" aria-label={`Service health: ${label}`}>
          <span className={`size-2 rounded-full ${tone}`} aria-hidden="true" />
          <span className="hidden sm:inline">{label}</span>
          <span className="sm:hidden">{health.status === "healthy" ? "OK" : health.status === "checking" ? "…" : "!"}</span>
        </span>
      </button>
      <section id="service-health-details" hidden={!open} aria-label="Service health details"
        className="absolute right-0 top-full z-50 w-80 max-w-[calc(100vw-2rem)] rounded-lg border border-zinc-200 bg-white p-3 text-sm shadow-xl dark:border-zinc-700 dark:bg-zinc-900">
        <div className="flex items-center justify-between gap-2">
          <h2 className="font-semibold">Service health</h2>
          <button type="button" aria-label="Close health details"
            className="rounded px-2 py-1 hover:bg-zinc-100 focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue-600 dark:hover:bg-zinc-800"
            onClick={() => { setPinned(false); setPreview(false); }}>Close</button>
        </div>
        <dl className="mt-2 space-y-2">
          <div><dt className="text-xs text-zinc-500">Status</dt><dd>{label}</dd></div>
          {health.details && <>
            <div><dt className="text-xs text-zinc-500">Version</dt><dd>{health.details.version}</dd></div>
            <div><dt className="text-xs text-zinc-500">Uptime</dt><dd>{health.details.uptime_seconds}s</dd></div>
            <div><dt className="text-xs text-zinc-500">Checked at (UTC)</dt><dd className="break-all">{health.details.timestamp}</dd></div>
          </>}
        </dl>
        {health.error && <p role="alert" className="mt-2 text-red-700 dark:text-red-300">{health.error}</p>}
        <p className="mt-2 text-xs text-zinc-500">Public process health · refreshes every 10 seconds. Dependency diagnostics require internal authentication.</p>
      </section>
    </div>
  );
}
