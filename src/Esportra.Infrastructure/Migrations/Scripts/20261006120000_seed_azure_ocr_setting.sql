-- Seed the Azure Computer Vision OCR toggle into system_settings.
-- Admins can flip this at runtime via PUT /api/admin/system-settings without restarting anything.

INSERT INTO system_settings (key, value, category, label, description, data_type, is_sensitive)
VALUES (
    'azure_ocr_enabled',
    'false',
    'integrations',
    'Azure Computer Vision OCR',
    'Use Azure Cognitive Services Vision Read API for scoreboard screenshot text detection. Falls back to on-device PaddleOCR when disabled.',
    'boolean',
    FALSE
)
ON CONFLICT (key) DO NOTHING;
