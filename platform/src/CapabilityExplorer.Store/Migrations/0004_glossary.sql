-- Human review assertions are isolated from immutable extractor observations.
-- JSONB retains exactly the supplied content; version/deleted are authoritative row state.
create table glossary_term (
    id uuid primary key,
    data jsonb not null,
    version bigint not null check (version > 0),
    deleted boolean not null default false,
    check (length(data->>'name') between 1 and 200),
    check (length(data->>'definition') between 1 and 10000),
    check (length(data->>'reviewer') between 1 and 200)
);

create table glossary_link (
    id uuid primary key,
    term_id uuid not null references glossary_term(id),
    data jsonb not null,
    version bigint not null check (version > 0),
    deleted boolean not null default false,
    check (data->>'role' in ('same', 'alias', 'derived_from')),
    check (length(data->>'reviewer') between 1 and 200),
    check (length(data->>'symbolKey') between 1 and 2000),
    check (data->'observed'->>'commitSha' ~ '^[0-9a-f]{40}$'),
    check (data->'observed'->>'artifactContentSha256' ~ '^[0-9a-f]{64}$')
);
create index glossary_link_term_idx on glossary_link(term_id) where not deleted;

create table glossary_issue (
    id uuid primary key,
    term_id uuid not null references glossary_term(id),
    data jsonb not null,
    version bigint not null check (version > 0),
    deleted boolean not null default false,
    check (data->>'kind' in ('type', 'derivation', 'naming', 'other')),
    check (data->>'status' in ('open', 'resolved')),
    check (length(data->>'summary') between 1 and 500),
    check (length(data->>'reviewer') between 1 and 200)
);
create index glossary_issue_term_idx on glossary_issue(term_id) where not deleted;

create table glossary_audit (
    sequence bigint generated always as identity primary key,
    entity_type text not null check (entity_type in ('term', 'link', 'issue')),
    entity_id uuid not null,
    version bigint not null,
    action text not null check (action in ('create', 'update', 'delete', 'delete_cascade')),
    actor text not null check (length(actor) between 1 and 200),
    data jsonb not null,
    recorded_at timestamptz not null default now(),
    unique(entity_type, entity_id, version)
);
create index glossary_audit_entity_idx on glossary_audit(entity_id, sequence);
