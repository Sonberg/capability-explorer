-- A name does not establish business semantics.
SELECT currency, SUM(amount) FROM example GROUP BY currency;
