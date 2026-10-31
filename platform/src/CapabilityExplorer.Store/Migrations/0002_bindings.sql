-- Stage 3: snapshot-level binding of @unbound hints. Bound edges are separate, insert-only rows that point back at
-- the extractor edge they were derived from. The edge, node and artifact tables are never touched by binding.

-- A snapshot is one set of artifacts bound with one bindings file. Its id is a hash of both, so saving the same
-- inputs twice is a no-op and different inputs never overwrite each other.
create table binding_snapshot (
    id            char(64) primary key,
    bindings_sha  char(64) not null,
    bindings      jsonb not null,
    created_at    timestamptz not null default now()
);

create table binding_snapshot_artifact (
    snapshot_id char(64) not null references binding_snapshot (id),
    artifact_id bigint not null references artifact (id),
    primary key (snapshot_id, artifact_id)
);

create table bound_edge (
    snapshot_id       char(64) not null references binding_snapshot (id),
    local_id          text not null,
    artifact_id       bigint not null,
    original_local_id text not null,
    from_key          text not null,
    relation          text not null,
    to_key            text not null,
    derivation        text not null check (derivation in ('rule', 'human')),
    resolution        text not null check (resolution in ('resolved', 'unresolved')),
    unresolved_reason text,
    rule              jsonb,
    evidence          text[] not null check (cardinality(evidence) > 0),
    status            text not null check (status in ('bound', 'broken')),
    hint              text not null,
    target            text not null,
    note              text,
    primary key (snapshot_id, local_id),
    foreign key (artifact_id, original_local_id) references edge (artifact_id, local_id),
    check (resolution <> 'unresolved' or unresolved_reason is not null)
);
create index bound_edge_to_idx on bound_edge (to_key, relation);
