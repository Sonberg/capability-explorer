export interface PreviewSource { repositoryId: string; commitSha: string; unitPath: string; artifactHash: string }
export interface PreviewHint { hint: string; existingTarget: string | null; occurrences: { source: PreviewSource; edgeId: string; relation: string; evidence: string[] }[] }
export interface PreviewCatalog { fingerprint: string; sources: PreviewSource[]; hints: PreviewHint[]; targets: string[] }
export interface PreviewEntry { hint: string; target: string; note: string; reviewed: boolean }
export interface PreviewResult { fingerprint: string; canExport: boolean; rows: { hint: string; target: string; state: string; message: string; reviewed: boolean; edgeCount: number }[] }
export type PreviewDraft = Record<string, PreviewEntry>;
export function editPreview(draft: PreviewDraft, hint: string, field: 'target' | 'note', value: string): PreviewDraft {
  return { ...draft, [hint]: { ...(draft[hint] ?? { hint, target: '', note: '', reviewed: false }), [field]: value, reviewed: false } };
}
export function reviewPreview(draft: PreviewDraft, hint: string, reviewed: boolean): PreviewDraft {
  const entry = draft[hint];
  return entry ? { ...draft, [hint]: { ...entry, reviewed } } : draft;
}
export function previewEntries(draft: PreviewDraft): PreviewEntry[] {
  return Object.values(draft).filter(e => e.target.trim().length > 0).sort((a,b) => a.hint < b.hint ? -1 : a.hint > b.hint ? 1 : 0);
}
export function canExportPreview(result: PreviewResult | undefined, fingerprint: string): boolean {
  return result?.fingerprint === fingerprint && result.canExport && result.rows.length > 0
    && result.rows.every(r => r.reviewed && ['evidenced', 'external'].includes(r.state));
}
