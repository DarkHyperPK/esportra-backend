CREATE TABLE IF NOT EXISTS public.lfg_saved_listings (
    user_id uuid NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    listing_id uuid NOT NULL REFERENCES public.lfg_listings(id) ON DELETE CASCADE,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, listing_id)
);

CREATE INDEX IF NOT EXISTS idx_lfg_saved_user_created
    ON public.lfg_saved_listings (user_id, created_at DESC, listing_id);

ALTER TABLE public.lfg_saved_listings ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS lfg_saved_select ON public.lfg_saved_listings;
CREATE POLICY lfg_saved_select ON public.lfg_saved_listings FOR SELECT
    TO authenticated USING (user_id = auth.uid());
DROP POLICY IF EXISTS lfg_saved_insert ON public.lfg_saved_listings;
CREATE POLICY lfg_saved_insert ON public.lfg_saved_listings FOR INSERT
    TO authenticated WITH CHECK (user_id = auth.uid() AND EXISTS (
        SELECT 1 FROM public.lfg_listings l WHERE l.id = listing_id
        AND l.status = 'published' AND (l.expires_at IS NULL OR l.expires_at > now())
    ));
DROP POLICY IF EXISTS lfg_saved_delete ON public.lfg_saved_listings;
CREATE POLICY lfg_saved_delete ON public.lfg_saved_listings FOR DELETE
    TO authenticated USING (user_id = auth.uid());
DROP POLICY IF EXISTS lfg_saved_service ON public.lfg_saved_listings;
GRANT SELECT, INSERT, DELETE ON public.lfg_saved_listings TO authenticated;
GRANT SELECT, INSERT, UPDATE, DELETE ON public.lfg_saved_listings TO service_role;

ALTER TABLE public.lfg_interests
    ADD COLUMN IF NOT EXISTS conversation_id uuid REFERENCES public.conversations(id) ON DELETE SET NULL;
