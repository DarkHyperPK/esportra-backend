-- Admin kill switch for scoreboard OCR. When 'false', the parse-screenshot endpoint
-- returns 503 and the frontend falls back to plain screenshot upload.
-- Toggle at runtime via PUT /api/admin/system-settings without restarting the API.
INSERT INTO system_settings (key, value, category, label, description, data_type, is_sensitive)
VALUES (
    'scoreboard_ocr_enabled',
    'false',
    'integrations',
    'Scoreboard OCR',
    'Enable scoreboard screenshot parsing. When disabled, players upload screenshots but no OCR or Riot verification runs.',
    'boolean',
    FALSE
)
ON CONFLICT (key) DO NOTHING;
