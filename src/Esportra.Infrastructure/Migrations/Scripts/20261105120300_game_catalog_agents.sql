-- Valorant agent -> role catalog used by Queue Now agent synergy. Not versioned like
-- game_catalog_regions/rank_tiers: one row per (game, agent). Seed generated from
-- https://valorant-api.com/v1/agents?isPlayableCharacter=true; ValorantStatsSyncService upserts new agents.

CREATE TABLE IF NOT EXISTS public.game_catalog_agents (
    id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    game_slug    TEXT NOT NULL,
    agent_id     TEXT NOT NULL,
    display_name TEXT NOT NULL,
    role_key     TEXT NOT NULL,
    icon_url     TEXT,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now()
);

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'game_catalog_agents_game_agent_unique') THEN
    ALTER TABLE public.game_catalog_agents
      ADD CONSTRAINT game_catalog_agents_game_agent_unique UNIQUE (game_slug, agent_id);
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_game_catalog_agents_role') THEN
    ALTER TABLE public.game_catalog_agents
      ADD CONSTRAINT chk_game_catalog_agents_role CHECK (role_key IN ('duelist', 'initiator', 'controller', 'sentinel'));
  END IF;
END $$;

INSERT INTO public.game_catalog_agents (game_slug, agent_id, display_name, role_key, icon_url) VALUES
  ('valorant', '41fb69c1-4189-7b37-f117-bcaf1e96f1bf', 'Astra', 'controller', 'https://media.valorant-api.com/agents/41fb69c1-4189-7b37-f117-bcaf1e96f1bf/displayicon.png'),
  ('valorant', '5f8d3a7f-467b-97f3-062c-13acf203c006', 'Breach', 'initiator', 'https://media.valorant-api.com/agents/5f8d3a7f-467b-97f3-062c-13acf203c006/displayicon.png'),
  ('valorant', '9f0d8ba9-4140-b941-57d3-a7ad57c6b417', 'Brimstone', 'controller', 'https://media.valorant-api.com/agents/9f0d8ba9-4140-b941-57d3-a7ad57c6b417/displayicon.png'),
  ('valorant', '22697a3d-45bf-8dd7-4fec-84a9e28c69d7', 'Chamber', 'sentinel', 'https://media.valorant-api.com/agents/22697a3d-45bf-8dd7-4fec-84a9e28c69d7/displayicon.png'),
  ('valorant', '1dbf2edd-4729-0984-3115-daa5eed44993', 'Clove', 'controller', 'https://media.valorant-api.com/agents/1dbf2edd-4729-0984-3115-daa5eed44993/displayicon.png'),
  ('valorant', '117ed9e3-49f3-6512-3ccf-0cada7e3823b', 'Cypher', 'sentinel', 'https://media.valorant-api.com/agents/117ed9e3-49f3-6512-3ccf-0cada7e3823b/displayicon.png'),
  ('valorant', 'cc8b64c8-4b25-4ff9-6e7f-37b4da43d235', 'Deadlock', 'sentinel', 'https://media.valorant-api.com/agents/cc8b64c8-4b25-4ff9-6e7f-37b4da43d235/displayicon.png'),
  ('valorant', 'dade69b4-4f5a-8528-247b-219e5a1facd6', 'Fade', 'initiator', 'https://media.valorant-api.com/agents/dade69b4-4f5a-8528-247b-219e5a1facd6/displayicon.png'),
  ('valorant', 'e370fa57-4757-3604-3648-499e1f642d3f', 'Gekko', 'initiator', 'https://media.valorant-api.com/agents/e370fa57-4757-3604-3648-499e1f642d3f/displayicon.png'),
  ('valorant', '95b78ed7-4637-86d9-7e41-71ba8c293152', 'Harbor', 'controller', 'https://media.valorant-api.com/agents/95b78ed7-4637-86d9-7e41-71ba8c293152/displayicon.png'),
  ('valorant', 'ddd5a15c-4fbe-42fa-3789-11a55ca5ee04', 'Hush', 'duelist', 'https://media.valorant-api.com/agents/ddd5a15c-4fbe-42fa-3789-11a55ca5ee04/displayicon.png'),
  ('valorant', '0e38b510-41a8-5780-5e8f-568b2a4f2d6c', 'Iso', 'duelist', 'https://media.valorant-api.com/agents/0e38b510-41a8-5780-5e8f-568b2a4f2d6c/displayicon.png'),
  ('valorant', 'add6443a-41bd-e414-f6ad-e58d267f4e95', 'Jett', 'duelist', 'https://media.valorant-api.com/agents/add6443a-41bd-e414-f6ad-e58d267f4e95/displayicon.png'),
  ('valorant', '601dbbe7-43ce-be57-2a40-4abd24953621', 'KAY/O', 'initiator', 'https://media.valorant-api.com/agents/601dbbe7-43ce-be57-2a40-4abd24953621/displayicon.png'),
  ('valorant', '1e58de9c-4950-5125-93e9-a0aee9f98746', 'Killjoy', 'sentinel', 'https://media.valorant-api.com/agents/1e58de9c-4950-5125-93e9-a0aee9f98746/displayicon.png'),
  ('valorant', '7c8a4701-4de6-9355-b254-e09bc2a34b72', 'Miks', 'controller', 'https://media.valorant-api.com/agents/7c8a4701-4de6-9355-b254-e09bc2a34b72/displayicon.png'),
  ('valorant', 'bb2a4828-46eb-8cd1-e765-15848195d751', 'Neon', 'duelist', 'https://media.valorant-api.com/agents/bb2a4828-46eb-8cd1-e765-15848195d751/displayicon.png'),
  ('valorant', '8e253930-4c05-31dd-1b6c-968525494517', 'Omen', 'controller', 'https://media.valorant-api.com/agents/8e253930-4c05-31dd-1b6c-968525494517/displayicon.png'),
  ('valorant', 'eb93336a-449b-9c1b-0a54-a891f7921d69', 'Phoenix', 'duelist', 'https://media.valorant-api.com/agents/eb93336a-449b-9c1b-0a54-a891f7921d69/displayicon.png'),
  ('valorant', 'f94c3b30-42be-e959-889c-5aa313dba261', 'Raze', 'duelist', 'https://media.valorant-api.com/agents/f94c3b30-42be-e959-889c-5aa313dba261/displayicon.png'),
  ('valorant', 'a3bfb853-43b2-7238-a4f1-ad90e9e46bcc', 'Reyna', 'duelist', 'https://media.valorant-api.com/agents/a3bfb853-43b2-7238-a4f1-ad90e9e46bcc/displayicon.png'),
  ('valorant', '569fdd95-4d10-43ab-ca70-79becc718b46', 'Sage', 'sentinel', 'https://media.valorant-api.com/agents/569fdd95-4d10-43ab-ca70-79becc718b46/displayicon.png'),
  ('valorant', '6f2a04ca-43e0-be17-7f36-b3908627744d', 'Skye', 'initiator', 'https://media.valorant-api.com/agents/6f2a04ca-43e0-be17-7f36-b3908627744d/displayicon.png'),
  ('valorant', '320b2a48-4d9b-a075-30f1-1f93a9b638fa', 'Sova', 'initiator', 'https://media.valorant-api.com/agents/320b2a48-4d9b-a075-30f1-1f93a9b638fa/displayicon.png'),
  ('valorant', 'b444168c-4e35-8076-db47-ef9bf368f384', 'Tejo', 'initiator', 'https://media.valorant-api.com/agents/b444168c-4e35-8076-db47-ef9bf368f384/displayicon.png'),
  ('valorant', '92eeef5d-43b5-1d4a-8d03-b3927a09034b', 'Veto', 'sentinel', 'https://media.valorant-api.com/agents/92eeef5d-43b5-1d4a-8d03-b3927a09034b/displayicon.png'),
  ('valorant', '707eab51-4836-f488-046a-cda6bf494859', 'Viper', 'controller', 'https://media.valorant-api.com/agents/707eab51-4836-f488-046a-cda6bf494859/displayicon.png'),
  ('valorant', 'efba5359-4016-a1e5-7626-b1ae76895940', 'Vyse', 'sentinel', 'https://media.valorant-api.com/agents/efba5359-4016-a1e5-7626-b1ae76895940/displayicon.png'),
  ('valorant', 'df1cb487-4902-002e-5c17-d28e83e78588', 'Waylay', 'duelist', 'https://media.valorant-api.com/agents/df1cb487-4902-002e-5c17-d28e83e78588/displayicon.png'),
  ('valorant', '7f94d92c-4234-0a36-9646-3a87eb8b5c89', 'Yoru', 'duelist', 'https://media.valorant-api.com/agents/7f94d92c-4234-0a36-9646-3a87eb8b5c89/displayicon.png')

ON CONFLICT (game_slug, agent_id) DO NOTHING;

ALTER TABLE public.game_catalog_agents ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS game_catalog_agents_public ON public.game_catalog_agents;
CREATE POLICY game_catalog_agents_public ON public.game_catalog_agents
    FOR SELECT TO authenticated USING (true);
DROP POLICY IF EXISTS game_catalog_agents_service ON public.game_catalog_agents;
CREATE POLICY game_catalog_agents_service ON public.game_catalog_agents
    FOR ALL TO service_role USING (true) WITH CHECK (true);
REVOKE ALL ON public.game_catalog_agents FROM anon;
REVOKE INSERT, UPDATE, DELETE ON public.game_catalog_agents FROM authenticated;
