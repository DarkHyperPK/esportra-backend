-- Players can report someone they met in a lobby. The CHECK on context gains 'lobby'.
ALTER TABLE public.squad_up_reports ADD COLUMN IF NOT EXISTS lobby_id UUID REFERENCES public.squad_up_lobbies(id) ON DELETE SET NULL;

ALTER TABLE public.squad_up_reports DROP CONSTRAINT IF EXISTS chk_squad_up_reports_context;
ALTER TABLE public.squad_up_reports ADD CONSTRAINT chk_squad_up_reports_context CHECK (context IN ('card', 'match', 'lobby'));
