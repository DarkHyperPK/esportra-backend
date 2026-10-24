-- DEBT-001: TEXT length CHECK constraints on free-text fields added in Phase 1.
-- All constraints wrapped in DO $$ guards (ADD CONSTRAINT has no IF NOT EXISTS).

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_player_game_profiles_notes_len') THEN
    ALTER TABLE public.player_game_profiles
      ADD CONSTRAINT chk_player_game_profiles_notes_len
      CHECK (notes IS NULL OR length(notes) <= 1000);
  END IF;
END $$;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_lfg_listings_description_len') THEN
    ALTER TABLE public.lfg_listings
      ADD CONSTRAINT chk_lfg_listings_description_len
      CHECK (description IS NULL OR length(description) <= 2000);
  END IF;
END $$;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_lfg_interests_message_len') THEN
    ALTER TABLE public.lfg_interests
      ADD CONSTRAINT chk_lfg_interests_message_len
      CHECK (message IS NULL OR length(message) <= 500);
  END IF;
END $$;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_player_avail_except_note_len') THEN
    ALTER TABLE public.player_availability_exceptions
      ADD CONSTRAINT chk_player_avail_except_note_len
      CHECK (note IS NULL OR length(note) <= 500);
  END IF;
END $$;
