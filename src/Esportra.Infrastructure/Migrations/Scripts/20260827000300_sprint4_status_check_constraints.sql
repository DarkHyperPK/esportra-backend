-- M9: CHECK constraint on tournament_stages.status
ALTER TABLE tournament_stages
    ADD CONSTRAINT chk_stage_status
    CHECK (status IN ('pending', 'upcoming', 'active', 'completed'));

-- M9: CHECK constraint on tournament_disputes.status
ALTER TABLE tournament_disputes
    ADD CONSTRAINT chk_dispute_status
    CHECK (status IN ('open', 'resolved', 'rejected'));
