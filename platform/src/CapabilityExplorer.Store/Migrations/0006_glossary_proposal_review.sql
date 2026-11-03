-- Decisions retain the proposal separately from accepted human assertions.
create table glossary_proposal_review (
    id uuid primary key,
    input_hash text not null check (input_hash ~ '^[0-9a-f]{64}$'),
    decision text not null check (decision in ('accepted', 'rejected')),
    reviewer text not null check (length(reviewer) between 1 and 200),
    data jsonb not null,
    recorded_at timestamptz not null default now()
);
