-- Derived and rebuildable: one vector per symbol (its latest occurrence) and embedding model, for similarity search.
-- Never a code fact; dropping this table loses nothing that `ce embed` cannot recompute.
create extension if not exists vector;

create table embedding (
    symbol_key    text not null,
    model         text not null,
    text_sha256   char(64) not null, -- of the embedded text: unchanged text is not embedded again
    repository_id text not null,
    node_kind     text not null,
    name          text not null,
    vec           vector not null,   -- dimension follows the model; only vectors of one model are compared
    embedded_at   timestamptz not null default now(),
    primary key (symbol_key, model)
);
-- ponytail: exact scan (no ANN index: hnsw needs a fixed dimension). Add one per model if search gets slow.
