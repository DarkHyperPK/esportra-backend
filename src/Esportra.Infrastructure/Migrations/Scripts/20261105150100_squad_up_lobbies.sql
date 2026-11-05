-- Squad Up Lobby mode: a host opens a lobby with open slots and wanted roles; compatible players request a slot,
-- the host accepts, and accepted players share one group chat. Written and read only through the API.
CREATE TABLE IF NOT EXISTS public.squad_up_lobbies (
    id               UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    host_id          UUID NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    game_slug        TEXT NOT NULL DEFAULT 'valorant',
    mode             TEXT NOT NULL DEFAULT 'competitive',
    region           TEXT NOT NULL,                          -- host's Riot shard; players on other shards cannot party
    country_code     TEXT NOT NULL,
    wanted_countries TEXT[] NOT NULL DEFAULT '{}',
    competitive_tier INT,                                    -- host's tier (NULL allowed for Unrated)
    min_tier         INT,
    max_tier         INT,
    title            TEXT NOT NULL DEFAULT '',
    open_slots       INT  NOT NULL,                          -- players the host still needs when the lobby opened
    wanted_roles     TEXT[] NOT NULL DEFAULT '{}',
    mic_required     BOOLEAN NOT NULL DEFAULT false,
    status           TEXT NOT NULL DEFAULT 'open',
    conversation_id  UUID REFERENCES public.conversations(id) ON DELETE SET NULL,
    last_heartbeat   TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_activity_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    closed_at        TIMESTAMPTZ
);

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_squad_up_lobbies_mode') THEN
    ALTER TABLE public.squad_up_lobbies ADD CONSTRAINT chk_squad_up_lobbies_mode CHECK (mode IN ('competitive', 'unrated'));
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_squad_up_lobbies_status') THEN
    ALTER TABLE public.squad_up_lobbies ADD CONSTRAINT chk_squad_up_lobbies_status CHECK (status IN ('open', 'full', 'closed'));
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_squad_up_lobbies_slots') THEN
    ALTER TABLE public.squad_up_lobbies ADD CONSTRAINT chk_squad_up_lobbies_slots CHECK (open_slots BETWEEN 1 AND 4);
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_squad_up_lobbies_title') THEN
    ALTER TABLE public.squad_up_lobbies ADD CONSTRAINT chk_squad_up_lobbies_title CHECK (char_length(title) <= 60);
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_squad_up_lobbies_rank_range') THEN
    ALTER TABLE public.squad_up_lobbies ADD CONSTRAINT chk_squad_up_lobbies_rank_range
      CHECK (min_tier IS NULL OR max_tier IS NULL OR min_tier <= max_tier);
  END IF;
END $$;

-- One open (or full) lobby per host at a time.
CREATE UNIQUE INDEX IF NOT EXISTS ux_squad_up_lobbies_one_per_host
    ON public.squad_up_lobbies (host_id) WHERE status IN ('open', 'full');
CREATE INDEX IF NOT EXISTS idx_squad_up_lobbies_board
    ON public.squad_up_lobbies (game_slug, region, mode, status, last_heartbeat DESC);

CREATE TABLE IF NOT EXISTS public.squad_up_lobby_members (
    lobby_id    UUID NOT NULL REFERENCES public.squad_up_lobbies(id) ON DELETE CASCADE,
    user_id     UUID NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    role_key    TEXT,
    status      TEXT NOT NULL DEFAULT 'requested',
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    decided_at  TIMESTAMPTZ,
    PRIMARY KEY (lobby_id, user_id)
);

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_squad_up_lobby_members_status') THEN
    ALTER TABLE public.squad_up_lobby_members ADD CONSTRAINT chk_squad_up_lobby_members_status
      CHECK (status IN ('requested', 'accepted', 'declined', 'left', 'kicked'));
  END IF;
END $$;
CREATE INDEX IF NOT EXISTS idx_squad_up_lobby_members_user ON public.squad_up_lobby_members (user_id, created_at DESC);

ALTER TABLE public.squad_up_lobbies ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS squad_up_lobbies_service ON public.squad_up_lobbies;
CREATE POLICY squad_up_lobbies_service ON public.squad_up_lobbies FOR ALL TO service_role USING (true) WITH CHECK (true);
REVOKE ALL ON public.squad_up_lobbies FROM anon, authenticated;

ALTER TABLE public.squad_up_lobby_members ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS squad_up_lobby_members_service ON public.squad_up_lobby_members;
CREATE POLICY squad_up_lobby_members_service ON public.squad_up_lobby_members FOR ALL TO service_role USING (true) WITH CHECK (true);
REVOKE ALL ON public.squad_up_lobby_members FROM anon, authenticated;
