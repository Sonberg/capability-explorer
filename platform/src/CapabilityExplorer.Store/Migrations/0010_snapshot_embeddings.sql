create table snapshot_embedding_projection (
    snapshot_id uuid not null references analysis_snapshot(id) on delete cascade,
    model_key char(64) not null check (model_key ~ '^[a-f0-9]{64}$'),
    model_id text not null,
    recipe_version text not null,
    artifact_count bigint not null check (artifact_count >= 0),
    artifacts_sha256 char(64) not null check (artifacts_sha256 ~ '^[a-f0-9]{64}$'),
    vector_dimensions integer check (vector_dimensions between 1 and 16000),
    enumeration_complete boolean not null default false,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now(),
    last_success timestamptz,
    error_code text check (length(error_code) between 1 and 64),
    primary key (snapshot_id, model_key)
);

create index snapshot_embedding_completed_idx
    on snapshot_embedding_projection (snapshot_id, last_success desc, model_key)
    where last_success is not null and enumeration_complete;

create table snapshot_embedding_artifact (
    snapshot_id uuid not null,
    model_key char(64) not null,
    artifact_id bigint not null,
    sealed boolean not null default false,
    total_count bigint not null default 0 check (total_count >= 0),
    ready_count bigint not null default 0 check (ready_count >= 0),
    pending_count bigint not null default 0 check (pending_count >= 0),
    failed_count bigint not null default 0 check (failed_count >= 0),
    last_success timestamptz,
    primary key (snapshot_id, model_key, artifact_id),
    foreign key (snapshot_id, model_key)
        references snapshot_embedding_projection(snapshot_id, model_key) on delete cascade,
    foreign key (snapshot_id, artifact_id)
        references analysis_snapshot_artifact(snapshot_id, artifact_id) on delete cascade,
    check (total_count = ready_count + pending_count + failed_count)
);

create table snapshot_embedding_item (
    snapshot_id uuid not null,
    model_key char(64) not null,
    artifact_id bigint not null,
    node_local_id text not null,
    text_sha256 char(64) not null check (text_sha256 ~ '^[a-f0-9]{64}$'),
    state text not null default 'pending' check (state in ('pending', 'ready', 'failed')),
    vec vector,
    completed_at timestamptz,
    attempts integer not null default 0 check (attempts >= 0),
    error_code text check (length(error_code) between 1 and 64),
    primary key (snapshot_id, model_key, artifact_id, node_local_id),
    foreign key (snapshot_id, model_key, artifact_id)
        references snapshot_embedding_artifact(snapshot_id, model_key, artifact_id) on delete cascade,
    foreign key (artifact_id, node_local_id)
        references node(artifact_id, local_id) on delete cascade,
    check ((state = 'ready' and vec is not null and completed_at is not null and error_code is null)
        or (state = 'pending' and vec is null and completed_at is null and error_code is null)
        or (state = 'failed' and vec is null and completed_at is null and error_code is not null))
);

create function protect_ready_snapshot_embedding() returns trigger language plpgsql as $$
begin
    if OLD.state = 'ready' and NEW is distinct from OLD then
        raise exception 'ready snapshot embedding occurrences are immutable';
    end if;
    return NEW;
end
$$;

create trigger snapshot_embedding_ready_immutable
    before update on snapshot_embedding_item
    for each row execute function protect_ready_snapshot_embedding();
