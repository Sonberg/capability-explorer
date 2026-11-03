create sequence deprecation_mark_version as bigint;

create table deprecation_mark (
    mark_key text primary key check (length(btrim(mark_key)) between 1 and 4096),
    status text not null check (status in ('deprecated', 'obsolete')),
    note text not null check (length(note) <= 4096),
    marked_at timestamptz not null check (isfinite(marked_at)),
    marked_at_tick smallint not null default 0 check (marked_at_tick between 0 and 9),
    version bigint not null default nextval('deprecation_mark_version') check (version > 0)
);

comment on table deprecation_mark is
    'Global curated integration/element marks, independent of analysis snapshots. Explicit legacy import is insert-only.';
comment on column deprecation_mark.version is
    'Sequence-backed optimistic concurrency token; never reused after deletion and recreation.';
comment on column deprecation_mark.marked_at_tick is
    'Final 100ns digit retained because PostgreSQL timestamptz has microsecond precision.';
