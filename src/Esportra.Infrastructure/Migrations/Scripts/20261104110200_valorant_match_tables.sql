-- Normalized VALORANT match data, derived from provider_match_payloads by the ingest job. Rows are replaced as a
-- whole per match on re-ingest, so they can always be rebuilt. Written only by the backend.
CREATE TABLE IF NOT EXISTS public.valorant_match_teams (
    match_id       uuid    NOT NULL REFERENCES public.provider_matches(id) ON DELETE CASCADE,
    team_id        text    NOT NULL,
    won            boolean,
    rounds_played  integer,
    rounds_won     integer,
    num_points     integer,
    PRIMARY KEY (match_id, team_id)
);

CREATE TABLE IF NOT EXISTS public.valorant_match_players (
    match_id           uuid    NOT NULL REFERENCES public.provider_matches(id) ON DELETE CASCADE,
    puuid              text    NOT NULL,
    riot_side          text    NOT NULL,
    game_name          text,
    tag_line           text,
    party_id           text,
    character_id       text,
    competitive_tier   integer,
    is_observer        boolean NOT NULL DEFAULT false,
    score              integer NOT NULL DEFAULT 0,
    rounds_played      integer NOT NULL DEFAULT 0,
    kills              integer NOT NULL DEFAULT 0,
    deaths             integer NOT NULL DEFAULT 0,
    assists            integer NOT NULL DEFAULT 0,
    playtime_ms        integer,
    grenade_casts      integer,
    ability1_casts     integer,
    ability2_casts     integer,
    ultimate_casts     integer,
    damage_dealt       integer NOT NULL DEFAULT 0,
    headshots          integer NOT NULL DEFAULT 0,
    bodyshots          integer NOT NULL DEFAULT 0,
    legshots           integer NOT NULL DEFAULT 0,
    first_kills        integer NOT NULL DEFAULT 0,
    first_deaths       integer NOT NULL DEFAULT 0,
    kills_2k           integer NOT NULL DEFAULT 0,
    kills_3k           integer NOT NULL DEFAULT 0,
    kills_4k           integer NOT NULL DEFAULT 0,
    kills_5k           integer NOT NULL DEFAULT 0,
    plants             integer NOT NULL DEFAULT 0,
    defuses            integer NOT NULL DEFAULT 0,
    PRIMARY KEY (match_id, puuid)
);
CREATE INDEX IF NOT EXISTS idx_vmp_puuid ON public.valorant_match_players (puuid);

CREATE TABLE IF NOT EXISTS public.valorant_match_rounds (
    match_id               uuid    NOT NULL REFERENCES public.provider_matches(id) ON DELETE CASCADE,
    round_num              integer NOT NULL,
    round_result           text,
    round_result_code      text,
    round_ceremony         text,
    winning_team           text,
    winning_team_role      text,
    bomb_planter           text,
    bomb_defuser           text,
    plant_round_time_ms    integer,
    plant_site             text,
    plant_x                integer,
    plant_y                integer,
    defuse_round_time_ms   integer,
    defuse_x               integer,
    defuse_y               integer,
    PRIMARY KEY (match_id, round_num)
);

CREATE TABLE IF NOT EXISTS public.valorant_round_player_stats (
    match_id        uuid    NOT NULL REFERENCES public.provider_matches(id) ON DELETE CASCADE,
    round_num       integer NOT NULL,
    puuid           text    NOT NULL,
    kills           integer NOT NULL DEFAULT 0,
    score           integer NOT NULL DEFAULT 0,
    loadout_value   integer,
    weapon          text,
    armor           text,
    spent           integer,
    remaining       integer,
    ability         jsonb,
    PRIMARY KEY (match_id, round_num, puuid)
);

CREATE TABLE IF NOT EXISTS public.valorant_round_damage (
    match_id    uuid    NOT NULL REFERENCES public.provider_matches(id) ON DELETE CASCADE,
    round_num   integer NOT NULL,
    puuid       text    NOT NULL,
    receiver    text    NOT NULL,
    damage      integer NOT NULL DEFAULT 0,
    headshots   integer NOT NULL DEFAULT 0,
    bodyshots   integer NOT NULL DEFAULT 0,
    legshots    integer NOT NULL DEFAULT 0,
    PRIMARY KEY (match_id, round_num, puuid, receiver)
);

CREATE TABLE IF NOT EXISTS public.valorant_match_kills (
    match_id               uuid    NOT NULL REFERENCES public.provider_matches(id) ON DELETE CASCADE,
    round_num              integer NOT NULL,
    kill_ordinal           integer NOT NULL,
    killer                 text,
    victim                 text    NOT NULL,
    assistants             text[]  NOT NULL DEFAULT ARRAY[]::text[],
    time_since_game_ms     integer,
    time_since_round_ms    integer,
    victim_x               integer,
    victim_y               integer,
    damage_type            text,
    damage_item            text,
    is_secondary_fire      boolean,
    player_locations       jsonb,
    PRIMARY KEY (match_id, round_num, kill_ordinal)
);
CREATE INDEX IF NOT EXISTS idx_vmk_killer ON public.valorant_match_kills (killer) WHERE killer IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_vmk_victim ON public.valorant_match_kills (victim);

ALTER TABLE public.valorant_match_teams ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.valorant_match_players ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.valorant_match_rounds ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.valorant_round_player_stats ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.valorant_round_damage ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.valorant_match_kills ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS vmt_service ON public.valorant_match_teams;
CREATE POLICY vmt_service ON public.valorant_match_teams FOR ALL TO service_role USING (true) WITH CHECK (true);
DROP POLICY IF EXISTS vmp_service ON public.valorant_match_players;
CREATE POLICY vmp_service ON public.valorant_match_players FOR ALL TO service_role USING (true) WITH CHECK (true);
DROP POLICY IF EXISTS vmr_service ON public.valorant_match_rounds;
CREATE POLICY vmr_service ON public.valorant_match_rounds FOR ALL TO service_role USING (true) WITH CHECK (true);
DROP POLICY IF EXISTS vrps_service ON public.valorant_round_player_stats;
CREATE POLICY vrps_service ON public.valorant_round_player_stats FOR ALL TO service_role USING (true) WITH CHECK (true);
DROP POLICY IF EXISTS vrd_service ON public.valorant_round_damage;
CREATE POLICY vrd_service ON public.valorant_round_damage FOR ALL TO service_role USING (true) WITH CHECK (true);
DROP POLICY IF EXISTS vmk_service ON public.valorant_match_kills;
CREATE POLICY vmk_service ON public.valorant_match_kills FOR ALL TO service_role USING (true) WITH CHECK (true);

REVOKE ALL ON public.valorant_match_teams FROM anon, authenticated;
REVOKE ALL ON public.valorant_match_players FROM anon, authenticated;
REVOKE ALL ON public.valorant_match_rounds FROM anon, authenticated;
REVOKE ALL ON public.valorant_round_player_stats FROM anon, authenticated;
REVOKE ALL ON public.valorant_round_damage FROM anon, authenticated;
REVOKE ALL ON public.valorant_match_kills FROM anon, authenticated;
GRANT SELECT, INSERT, UPDATE, DELETE ON public.valorant_match_teams TO service_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON public.valorant_match_players TO service_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON public.valorant_match_rounds TO service_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON public.valorant_round_player_stats TO service_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON public.valorant_round_damage TO service_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON public.valorant_match_kills TO service_role;
