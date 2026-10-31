CREATE SCHEMA pricing;

CREATE TABLE pricing.prices
(
    article_id text       PRIMARY KEY,
    amount_ore bigint     NOT NULL,
    currency   char(3)    NOT NULL DEFAULT 'SEK'
);

CREATE TABLE pricing.sales_ledger
(
    id         bigserial  PRIMARY KEY,
    article_id text       NOT NULL,
    sold_at    timestamptz NOT NULL,
    amount_ore bigint     NOT NULL,
    quantity   integer    NOT NULL,
    currency   char(3)    NOT NULL
);

CREATE TABLE pricing.demand_signals
(
    article_id       text        NOT NULL,
    average_per_week numeric     NOT NULL,
    received_at      timestamptz NOT NULL
);
