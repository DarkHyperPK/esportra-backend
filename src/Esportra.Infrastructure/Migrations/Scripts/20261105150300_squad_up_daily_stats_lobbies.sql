-- Durable lobby counters for the Admin Centre and the public Squad Up page.
ALTER TABLE public.squad_up_daily_stats ADD COLUMN IF NOT EXISTS lobbies_opened INT NOT NULL DEFAULT 0;
ALTER TABLE public.squad_up_daily_stats ADD COLUMN IF NOT EXISTS lobbies_filled INT NOT NULL DEFAULT 0;
