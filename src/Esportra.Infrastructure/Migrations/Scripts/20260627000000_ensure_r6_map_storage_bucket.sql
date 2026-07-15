-- Ensure public game-asset bucket accepts AVIF map images for Rainbow Six Siege.

INSERT INTO storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
VALUES (
    'system.assets.games',
    'system.assets.games',
    true,
    10485760,
    ARRAY['image/jpeg','image/png','image/webp','image/avif','image/gif','image/svg+xml']
)
ON CONFLICT (id) DO UPDATE
SET
    public = EXCLUDED.public,
    file_size_limit = EXCLUDED.file_size_limit,
    allowed_mime_types = EXCLUDED.allowed_mime_types;
