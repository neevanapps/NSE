-- One read-only sample of the published live marks (sampled repeatedly by tools\Measure-LivePnlLatency.ps1). No trailing semicolon: the script appends \watch.
-- quote_to_published_ms: quote available -> the publisher's sample instant. quote_to_readable_ms: quote available -> this read sees the row (what a Dashboard read could show).
SELECT clock_timestamp() AS observed_at,
       m.position_id,
       m.payload_json::jsonb ->> 'QuoteTickId' AS quote_tick_id,
       round(extract(epoch FROM to_timestamp((m.published_utc_ticks - 621355968000000000) / 10000000.0) - (m.payload_json::jsonb ->> 'QuoteAvailableAt')::timestamptz) * 1000) AS quote_to_published_ms,
       round(extract(epoch FROM clock_timestamp() - (m.payload_json::jsonb ->> 'QuoteAvailableAt')::timestamptz) * 1000) AS quote_to_readable_ms
FROM pev2.pev2_live_marks AS m
WHERE m.position_id <> '_publisher' AND m.status = 'Fresh'
