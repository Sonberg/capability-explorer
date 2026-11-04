create table model_product (
    id text primary key check (id ~ '^model:[a-f0-9]{64}$'),
    profile text not null check (profile in ('ce.models.v1','ce.candidates.v1')),
    repository_id text not null,
    commit_sha char(40) not null,
    unit_path text not null,
    artifact_sha256 char(64) not null,
    model_kind text not null check (model_kind in ('bpmn','dmn','candidate')),
    coverage text not null check (coverage in ('full','partial')),
    diagnostic_count integer not null check (diagnostic_count >= 0),
    source_sha256 char(64) not null,
    evidence_bytes bytea not null check (octet_length(evidence_bytes) between 1 and 16777216),
    foreign key (repository_id,commit_sha) references revision(repository_id,commit_sha)
);

create table model_source_product (
    sha256 char(64) primary key,
    envelope_bytes bytea not null check (octet_length(envelope_bytes) between 1 and 16777216),
    source_bytes bytea not null check (octet_length(source_bytes) between 1 and 1048576)
);

create table analysis_snapshot_model (
    snapshot_id uuid not null references analysis_snapshot(id),
    model_id text not null references model_product(id),
    collection_identity char(64) not null,
    source_status text not null check (source_status in ('available','missing','denied')),
    source_product_sha256 char(64) references model_source_product(sha256),
    primary key(snapshot_id,model_id),
    check ((source_status='available') = (source_product_sha256 is not null))
);

create function protect_model_snapshot_membership() returns trigger language plpgsql as $$
begin
    if TG_OP <> 'INSERT' then
        raise exception 'model products and snapshot membership are immutable';
    end if;
    if TG_TABLE_NAME = 'analysis_snapshot_model' and
       exists(select 1 from analysis_snapshot where id=NEW.snapshot_id and status <> 'candidate') then
        raise exception 'published model membership is immutable';
    end if;
    return NEW;
end
$$;
create trigger model_product_immutable before update or delete on model_product
    for each row execute function protect_model_snapshot_membership();
create trigger model_source_immutable before update or delete on model_source_product
    for each row execute function protect_model_snapshot_membership();
create trigger snapshot_model_immutable before insert or update or delete on analysis_snapshot_model
    for each row execute function protect_model_snapshot_membership();
