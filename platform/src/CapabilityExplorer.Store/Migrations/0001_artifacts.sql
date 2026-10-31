-- Stage 1: immutable extractor artifacts. Code facts are insert-only; nothing in this schema is ever updated
-- by interpretation, review or later analysis. Snapshots, lineage and review tables arrive in later stages.

create table repository (
    id             text primary key check (id ~ '^[a-z0-9][a-z0-9._-]{0,99}$'),
    url            text,
    default_branch text,
    created_at     timestamptz not null default now()
);

create table revision (
    repository_id text not null references repository (id),
    commit_sha    char(40) not null check (commit_sha ~ '^[0-9a-f]{40}$'),
    created_at    timestamptz not null default now(),
    primary key (repository_id, commit_sha)
);

-- One row per analysed unit (project) at one revision with one extractor + configuration.
-- The unique key is the analysis key: re-importing it is a no-op if the content is identical, and an error otherwise.
create table artifact (
    id                bigint generated always as identity primary key,
    repository_id     text not null,
    commit_sha        char(40) not null,
    unit_path         text not null,
    unit_name         text not null,
    language          text not null,
    extractor_name    text not null,
    extractor_version text not null,
    config_hash       char(64) not null,
    input_hash        char(64) not null,
    status            text not null check (status in ('full', 'partial')),
    content_sha256    char(64) not null,
    header            jsonb not null,
    complete          jsonb not null,
    imported_at       timestamptz not null default now(),
    foreign key (repository_id, commit_sha) references revision (repository_id, commit_sha),
    constraint artifact_analysis_key unique (repository_id, commit_sha, unit_path, extractor_name, extractor_version, config_hash)
);

-- Lets later stages reuse an artifact for an unchanged unit at another revision.
create index artifact_reuse_idx on artifact (repository_id, unit_path, input_hash, extractor_name, extractor_version, config_hash);

create table evidence (
    artifact_id    bigint not null references artifact (id),
    local_id       text not null,
    repository_id  text not null,
    commit_sha     char(40) not null,
    path           text not null,
    start_line     int not null,
    start_column   int not null,
    end_line       int not null,
    end_column     int not null,
    snippet_sha256 char(64) not null,
    extractor      jsonb not null,
    primary key (artifact_id, local_id)
);

-- A node row is an occurrence: (artifact, symbol_key) identifies one observation of a symbol.
create table node (
    artifact_id bigint not null references artifact (id),
    local_id    text not null,
    symbol_key  text not null,
    node_kind   text not null,
    name        text not null,
    signature   text,
    generated   boolean not null default false,
    props       jsonb,
    evidence    text[] not null check (cardinality(evidence) > 0),
    primary key (artifact_id, local_id),
    unique (artifact_id, symbol_key)
);
create index node_symbol_key_idx on node (symbol_key);

create table edge (
    artifact_id       bigint not null references artifact (id),
    local_id          text not null,
    from_key          text not null,
    relation          text not null,
    to_key            text,
    derivation        text not null check (derivation in ('compiler', 'rule', 'human')),
    resolution        text not null check (resolution in ('resolved', 'static-target', 'candidate', 'unresolved')),
    unresolved_reason text,
    rule              jsonb,
    props             jsonb,
    evidence          text[] not null check (cardinality(evidence) > 0),
    primary key (artifact_id, local_id),
    check (resolution = 'unresolved' or to_key is not null),
    check (resolution <> 'unresolved' or unresolved_reason is not null)
);
create index edge_from_idx on edge (from_key, relation);
create index edge_to_idx on edge (to_key, relation);

create table calculation (
    artifact_id bigint not null references artifact (id),
    local_id    text not null,
    symbol_key  text not null,
    ir_hash     char(64),
    body        jsonb not null,
    primary key (artifact_id, local_id)
);
create index calculation_symbol_key_idx on calculation (symbol_key);
create index calculation_ir_hash_idx on calculation (ir_hash);

create table diagnostic (
    artifact_id      bigint not null references artifact (id),
    local_id         text not null,
    severity         text not null,
    code             text not null,
    message          text not null,
    path             text,
    affects_coverage boolean not null default false,
    evidence         text[],
    primary key (artifact_id, local_id)
);
