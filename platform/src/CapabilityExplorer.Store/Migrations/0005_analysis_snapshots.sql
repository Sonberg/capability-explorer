-- Immutable selected analyses; publication is a short compare-and-swap transaction.
-- '*' is the workspace scope; other values are explicit capability IDs.
-- The same derived local edge ID can occur at multiple revisions. Preserve every
-- occurrence instead of silently dropping another artifact's binding evidence.
alter table bound_edge drop constraint bound_edge_pkey;
alter table bound_edge add primary key (snapshot_id, artifact_id, local_id);

create table analysis_snapshot (
    id uuid primary key,
    capability_id text not null check (capability_id = '*' or capability_id ~ '^[a-z0-9][a-z0-9._-]{0,99}$'),
    status text not null check (status in ('candidate', 'published')),
    input_identity text not null check (length(input_identity) between 1 and 1024),
    generation bigint check (generation > 0),
    created_at timestamptz not null default now(),
    published_at timestamptz,
    unique (id, capability_id, generation),
    unique (capability_id, input_identity),
    check ((status = 'candidate' and generation is null and published_at is null)
        or (status = 'published' and generation is not null and published_at is not null))
);

create table analysis_snapshot_artifact (
    snapshot_id uuid not null references analysis_snapshot(id),
    artifact_id bigint not null references artifact(id),
    repository_id text not null,
    commit_sha char(40) not null,
    unit_path text not null,
    primary key (snapshot_id, artifact_id),
    unique (snapshot_id, repository_id, commit_sha, unit_path)
);

create table published_snapshot (
    capability_id text primary key,
    snapshot_id uuid not null,
    generation bigint not null check (generation > 0),
    foreign key (snapshot_id, capability_id, generation)
        references analysis_snapshot(id, capability_id, generation)
);

create table snapshot_revision (
    snapshot_id uuid not null references analysis_snapshot(id),
    repository_id text not null,
    commit_sha char(40) not null,
    label text not null,
    subject text,
    commit_time timestamptz,
    is_default boolean not null default false,
    primary key (snapshot_id, repository_id, commit_sha)
);

create unique index snapshot_default_revision_idx
    on snapshot_revision (snapshot_id, repository_id) where is_default;

create table analysis_snapshot_binding (
    snapshot_id uuid primary key references analysis_snapshot(id),
    binding_snapshot_id char(64) not null references binding_snapshot(id)
);

create table snapshot_problem (
    snapshot_id uuid not null references analysis_snapshot(id),
    local_id bigint generated always as identity,
    category text not null,
    message text not null check (length(message) <= 4096),
    primary key (snapshot_id, local_id)
);

-- Artifact ID leads every exact-snapshot adjacency access. No trigram/large JSON indexes
-- are installed speculatively: search index choices require measured query plans.
create index edge_artifact_from_idx on edge (artifact_id, from_key, local_id collate "C", relation);
create index edge_artifact_to_idx on edge (artifact_id, to_key, local_id collate "C", relation) where to_key is not null;

-- Rebuildable deterministic search metadata; raw node observations remain insert-only.
-- .NET invariant folds and UTF-8 scalar sort bytes preserve the pinned invariant runtime's ignore-case behavior
-- without relying on the PostgreSQL server's locale or Unicode case tables.
create table node_search (
    artifact_id bigint not null,
    local_id text not null,
    normalizer_id text not null,
    name_fold text not null,
    symbol_fold text not null,
    name_sort bytea not null,
    primary key (artifact_id, local_id, normalizer_id),
    foreign key (artifact_id, local_id) references node(artifact_id, local_id)
);

-- Query readiness is artifact metadata, never a whole-node integrity scan per request.
create table artifact_search_projection (
    artifact_id bigint not null references artifact(id),
    normalizer_id text not null,
    completed_at timestamptz not null default now(),
    primary key (artifact_id, normalizer_id)
);

comment on table analysis_snapshot is 'A versioned immutable artifact selection; optional derived data is not a publication prerequisite.';
