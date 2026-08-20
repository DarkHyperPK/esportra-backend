-- Allow the same sponsor to occupy multiple slots in the same zone.
-- The per-slot uniqueness index remains (one placement per slot).
DROP INDEX IF EXISTS public.sponsor_placements_sponsor_scope_zone_unique_idx;
