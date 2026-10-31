CREATE FUNCTION pricing.weekly_revenue(p_article_id text, p_from date, p_to date)
RETURNS numeric
LANGUAGE sql
STABLE
AS $$
    SELECT ROUND(
               SUM(s.amount_ore * s.quantity) / 100.0
               / NULLIF(COUNT(DISTINCT date_trunc('week', s.sold_at)), 0),
               2)
    FROM pricing.sales_ledger AS s
    WHERE s.article_id = p_article_id
      AND s.sold_at >= p_from
      AND s.sold_at < p_to + 1
$$;
