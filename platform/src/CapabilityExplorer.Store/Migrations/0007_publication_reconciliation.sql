create table analysis_publication_attempt (
    run_id char(32) not null check (run_id ~ '^[0-9a-f]{32}$'),
    attempt_id char(32) not null check (attempt_id ~ '^[0-9a-f]{32}$'),
    capability_id text not null check (capability_id = '*' or capability_id ~ '^[a-z0-9][a-z0-9._-]{0,99}$'),
    accepted_inputs_hash char(64) not null check (accepted_inputs_hash ~ '^[0-9a-f]{64}$'),
    manifest_hash char(64) not null check (manifest_hash ~ '^[0-9a-f]{64}$'),
    expected_generation bigint not null check (expected_generation >= 0),
    expected_artifact_count integer not null check (expected_artifact_count between 1 and 100000),
    selection_sha256 char(64) not null check (selection_sha256 ~ '^[0-9a-f]{64}$'),
    candidate_snapshot_id uuid references analysis_snapshot(id),
    input_identity text check (input_identity is null or length(input_identity) between 1 and 1024),
    created_at timestamptz not null default now(),
    primary key (run_id, attempt_id),
    check ((candidate_snapshot_id is null) = (input_identity is null))
);

create table analysis_publication_attempt_artifact (
    run_id char(32) not null,
    attempt_id char(32) not null,
    artifact_index integer not null check (artifact_index >= 0),
    repository_id text not null,
    commit_sha char(40) not null,
    unit_path text not null,
    extractor_name text not null,
    extractor_version text not null,
    config_hash char(64) not null,
    content_sha256 char(64) not null check (content_sha256 ~ '^[0-9a-f]{64}$'),
    source_sha256 char(64) not null check (source_sha256 ~ '^[0-9a-f]{64}$'),
    import_state text not null check (import_state in ('pending','imported','reused','conflict','missing','invalid')),
    artifact_id bigint references artifact(id),
    observed_content_sha256 char(64) check (observed_content_sha256 is null or observed_content_sha256 ~ '^[0-9a-f]{64}$'),
    primary key (run_id, attempt_id, artifact_index),
    foreign key (run_id, attempt_id) references analysis_publication_attempt(run_id, attempt_id),
    check ((import_state in ('imported','reused') and artifact_id is not null
            and observed_content_sha256 is not null and observed_content_sha256 = content_sha256)
        or (import_state = 'conflict' and artifact_id is null and observed_content_sha256 is not null)
        or (import_state in ('pending','missing','invalid') and artifact_id is null))
);

create table analysis_publication_attempt_result (
    run_id char(32) not null,
    attempt_id char(32) not null,
    outcome text not null check (outcome in ('published','not_published')),
    reason text not null check (reason in ('committed','stale_generation','prepublication_failure')),
    snapshot_id uuid references analysis_snapshot(id),
    publication_generation bigint check (publication_generation is null or publication_generation > 0),
    recorded_at timestamptz not null default now(),
    primary key (run_id, attempt_id),
    foreign key (run_id, attempt_id) references analysis_publication_attempt(run_id, attempt_id),
    check ((outcome = 'published' and reason = 'committed' and snapshot_id is not null and publication_generation is not null)
        or (outcome = 'not_published' and reason <> 'committed' and publication_generation is null))
);

comment on table analysis_publication_attempt is
    'Durable per-run publication identity. Absence of a terminal result is unresolved; this record is not execution authorization.';
comment on table analysis_publication_attempt_result is
    'Atomic attempt-specific publication evidence. A published result is committed with the pointer CAS; a terminal rejection is committed without pointer mutation.';
