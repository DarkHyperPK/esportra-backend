# Optional Tournament Check-In

Make tournament-level check-in an organizer-configurable option rather than a hardcoded mandatory requirement. When disabled, the entire check-in phase is skipped and all approved participants are eligible for bracket seeding.

## Context

The `check_in_required` boolean already exists on the `tournaments` table and is accepted in create/update DTOs. However:
- The frontend hardcodes it to `true` (forces the value in a useEffect, shows "Mandatory" badges)
- The backend bracket generation endpoint (`POST /api/brackets/generate`) does not validate participant eligibility server-side for standard tournaments — it accepts whatever teams the frontend sends
- Stage config UI shows "only checked-in teams will participate" unconditionally

## Design

### Backend

#### 1. Generalize `BrSeedEligibility` → `SeedEligibility`

Move eligibility logic from `Esportra.Core.Br.BrSeedEligibility` to `Esportra.Core.Tournaments.SeedEligibility`. Same API surface:

- `IsCheckInRequiredAsync(conn, tournamentId, tx?)` — reads `check_in_required` from tournaments table
- `ResolveStatuses(checkInRequired)` — returns `["checked_in"]` when true, `["approved", "checked_in"]` when false
- `ParticipantSeedMessage(checkInRequired)` / `TeamSeedMessage(checkInRequired)` — error messages

`BrSeedEligibility` delegates to `SeedEligibility` internally (thin wrapper for backwards compatibility in BR endpoints).

#### 2. Backend validation in bracket generation

`POST /api/brackets/generate` (BracketEndpoints.cs):

After receiving `req.Teams`, before generating:
1. Look up `check_in_required` for the tournament via `SeedEligibility.IsCheckInRequiredAsync`
2. Query `tournament_participants` for the submitted team IDs
3. Validate each team's status against `SeedEligibility.ResolveStatuses(checkInRequired)`
4. Return 400 with descriptive error if any submitted team is ineligible

#### 3. Guard the check-in endpoint

`POST /api/tournaments/{id}/check-in`:
- If `check_in_required = false` → return 400: "Check-in is not enabled for this tournament."

#### 4. Guard the remove-unchecked endpoint

`POST /api/tournaments/{id}/remove-unchecked`:
- If `check_in_required = false` → return 400: "Check-in is not enabled for this tournament."

#### 5. Deadline job scheduling

Already conditional on `check_in_required` + `check_in_deadline` + `auto_remove_unchecked`. No change needed.

#### 6. Mock participant generation

`POST /api/tournaments/{id}/mock/generate`:
- When `check_in_required = false`: generate mocks with status `approved` (not `checked_in`)
- When `check_in_required = true`: keep current behavior (status `checked_in`)

### Frontend

#### 1. Tournament Creation Wizard (`StepRegistration.tsx`)

- Remove the `useEffect` that forces `checkInRequired: true` and `autoRemoveUnchecked: true`
- Replace "Mandatory" badges with a Switch toggle for "Require Check-In"
- When OFF: hide check-in window config, auto-remove section, deadline fields
- When ON: show current UI unchanged

#### 2. Organizer Dashboard (`TournamentManage.tsx`)

- **Check-In Monitor card**: only render when `check_in_required === true`
- **Settings tab**: conditional messaging
  - Enabled: "Only checked-in teams will be added to the bracket"
  - Disabled: "Check-in is not required — all approved teams are eligible"
- **"Remove Unchecked Teams" button**: hide when disabled

#### 3. Stage Setup Wizard (`StageSetupWizard.tsx`)

- `use_check_in_only` checkbox: hide when `check_in_required = false`
- Auto-set `use_check_in_only = false` when check-in is disabled

#### 4. Participant Detail Page (`Details.tsx`)

- Hide "CONFIRM PRESENCE" button and countdown when `check_in_required = false`
- `canSelfCheckIn` short-circuits to `false`

#### 5. Stage Management Tab (`StageManagementTab.tsx`)

- When generating bracket with check-in disabled: fetch all approved+checked_in participants
- Hide "N entries still pending check-in" toast when disabled

#### 6. Validation schema (`tournamentSchema.ts`)

- `checkInWindowMinutes` validation (5-120) only when `checkInRequired = true`
- Remove unconditional enforcement of `checkInRequired: true` from defaults

## Edge Cases

### Toggling check-in after participants exist

**Disabling** after some have checked in:
- Cancel scheduled deadline job (handled in update endpoint)
- Participants retain `checked_in` status (harmless — both `approved` and `checked_in` qualify when disabled)
- No data cleanup needed

**Enabling** on a tournament with approved participants:
- Participants remain `approved` until they explicitly check in
- Deadline job scheduled if deadline is provided

### BR tournaments

No change to BR behavior. `BrSeedEligibility` will delegate to the new `SeedEligibility` so logic stays DRY.

### Tournament status lifecycle

With check-in disabled:
```
Registration → Bracket Generation → Ongoing
```
No intermediate check-in phase. Status transitions based on `start_date` only.

## Defaults

New tournaments default to `check_in_required = false` (check-in disabled). Organizers opt in by toggling the switch during creation. This matches "optional" semantics — check-in is an opt-in feature, not opt-out.

## Files to Modify

### Backend (d:\esportra-backend)
- `src/Esportra.Core/Tournaments/SeedEligibility.cs` — NEW (generalized from BrSeedEligibility)
- `src/Esportra.Core/Br/BrSeedEligibility.cs` — delegate to SeedEligibility
- `src/Esportra.Api/Endpoints/BracketEndpoints.cs` — add eligibility validation
- `src/Esportra.Api/Endpoints/TournamentEndpoints.cs` — guard check-in + remove-unchecked endpoints, conditional mock status
- No migration needed (field already exists)

### Frontend (D:\frag-and-book-main)
- `src/components/tournament/wizard/StepRegistration.tsx` — toggle instead of mandatory
- `src/pages/organizer/TournamentManage.tsx` — conditional check-in monitor + settings text
- `src/components/organizer/wizard/StageSetupWizard.tsx` — hide use_check_in_only when disabled
- `src/components/organizer/tabs/StageManagementTab.tsx` — conditional participant filtering
- `src/pages/tournaments/Details.tsx` — hide check-in UI when disabled
- `src/schemas/tournamentSchema.ts` — conditional validation
- `src/types/tournamentWizard.ts` — change default to `false`
