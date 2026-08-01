# Optional Tournament Check-In Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make tournament-level check-in an organizer-configurable option, with backend validation ensuring only eligible participants are seeded into brackets.

**Architecture:** Generalize `BrSeedEligibility` into a shared `SeedEligibility` class in `Esportra.Core.Tournaments`. Add server-side eligibility validation to the bracket generation endpoint. Guard check-in and remove-unchecked endpoints when check-in is disabled. Frontend replaces hardcoded mandatory check-in with a toggle and conditionally renders all check-in UI.

**Tech Stack:** .NET 9 / Dapper / PostgreSQL (backend), React / TypeScript / Zod (frontend)

## Global Constraints

- Dapper only — no EF Core
- Parameterized SQL — never concatenate user input
- `check_in_required` column already exists on `tournaments` table (boolean, default `false`) — no migration needed
- Conventional commits: `feat:`, `fix:`, `refactor:` — first line < 72 chars
- Backend working directory: `d:\esportra-backend`
- Frontend working directory: `D:\frag-and-book-main`

---

### Task 1: Create shared `SeedEligibility` class

**Files:**
- Create: `src/Esportra.Core/Tournaments/SeedEligibility.cs`
- Modify: `src/Esportra.Core/Br/BrSeedEligibility.cs`

**Interfaces:**
- Consumes: `IDbConnection`, `Guid tournamentId`
- Produces: `SeedEligibility.IsCheckInRequiredAsync(conn, tournamentId, tx?)` returns `Task<bool>`, `SeedEligibility.ResolveStatuses(bool checkInRequired)` returns `string[]`, `SeedEligibility.ParticipantSeedMessage(bool)` returns `string`, `SeedEligibility.TeamSeedMessage(bool)` returns `string`

- [ ] **Step 1: Create `SeedEligibility.cs`**

```csharp
// src/Esportra.Core/Tournaments/SeedEligibility.cs
using System.Data;
using Dapper;

namespace Esportra.Core.Tournaments;

public static class SeedEligibility
{
    public static readonly string[] DefaultStatuses = ["approved", "checked_in"];
    public static readonly string[] CheckInRequiredStatuses = ["checked_in"];

    public static async Task<bool> IsCheckInRequiredAsync(
        IDbConnection conn,
        Guid tournamentId,
        IDbTransaction? tx = null)
    {
        return await conn.ExecuteScalarAsync<bool>(
            """
            SELECT COALESCE(check_in_required, false)
            FROM public.tournaments
            WHERE id = @tournamentId
            """,
            new { tournamentId },
            tx);
    }

    public static string[] ResolveStatuses(bool checkInRequired) =>
        checkInRequired ? CheckInRequiredStatuses : DefaultStatuses;

    public static string ParticipantSeedMessage(bool checkInRequired) =>
        checkInRequired
            ? "No checked-in participants found. Participants must check in before seeding."
            : "No eligible participants found. Participants must be approved or checked in.";

    public static string TeamSeedMessage(bool checkInRequired) =>
        checkInRequired
            ? "No checked-in teams found. Teams must check in before seeding."
            : "No eligible teams found. Teams must be approved or checked in.";
}
```

- [ ] **Step 2: Refactor `BrSeedEligibility` to delegate to `SeedEligibility`**

Replace the body of `src/Esportra.Core/Br/BrSeedEligibility.cs` with:

```csharp
using System.Data;
using Esportra.Core.Tournaments;

namespace Esportra.Core.Br;

public static class BrSeedEligibility
{
    public static readonly string[] DefaultStatuses = SeedEligibility.DefaultStatuses;
    public static readonly string[] CheckInRequiredStatuses = SeedEligibility.CheckInRequiredStatuses;

    public static Task<bool> IsCheckInRequiredAsync(
        IDbConnection conn,
        Guid tournamentId,
        IDbTransaction? tx = null) =>
        SeedEligibility.IsCheckInRequiredAsync(conn, tournamentId, tx);

    public static string[] ResolveStatuses(bool checkInRequired) =>
        SeedEligibility.ResolveStatuses(checkInRequired);

    public static string ParticipantSeedMessage(bool checkInRequired) =>
        SeedEligibility.ParticipantSeedMessage(checkInRequired);

    public static string TeamSeedMessage(bool checkInRequired) =>
        SeedEligibility.TeamSeedMessage(checkInRequired);
}
```

- [ ] **Step 3: Build and verify no compilation errors**

Run: `dotnet build d:\esportra-backend`
Expected: Build succeeded with 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/Esportra.Core/Tournaments/SeedEligibility.cs src/Esportra.Core/Br/BrSeedEligibility.cs
git commit -m "$(cat <<'EOF'
refactor: extract shared SeedEligibility from BrSeedEligibility

Generalizes check-in eligibility logic for use across all tournament
types, not just BR. BrSeedEligibility now delegates to the shared class.
EOF
)"
```

---

### Task 2: Add server-side eligibility validation to bracket generation

**Files:**
- Modify: `src/Esportra.Api/Endpoints/BracketEndpoints.cs:32-68`

**Interfaces:**
- Consumes: `SeedEligibility.IsCheckInRequiredAsync`, `SeedEligibility.ResolveStatuses`, `SeedEligibility.TeamSeedMessage`, `IDbConnectionFactory`
- Produces: 400 response with error message when ineligible teams are submitted

- [ ] **Step 1: Add `using` and `IDbConnectionFactory` parameter**

In `src/Esportra.Api/Endpoints/BracketEndpoints.cs`, add the using statement at the top (after existing usings):

```csharp
using Esportra.Core.Tournaments;
```

Add `IDbConnectionFactory db` to the endpoint's parameter list (after `CancellationToken ct`):

```csharp
app.MapPost("/api/brackets/generate", async (
    HttpContext ctx,
    [FromBody] GenerateBracketRequest req,
    BracketPersistenceService persistence,
    TournamentAuthorizationService tournamentAuth,
    IHubContext<BracketHub> bracketHub,
    IDbConnectionFactory db,
    CancellationToken ct) =>
{
```

- [ ] **Step 2: Add eligibility validation after the `teams.Count < 2` check (after line 69)**

Insert after `if (teams.Count < 2) return Results.BadRequest(...)`:

```csharp
            // Validate team eligibility based on check-in requirement
            using var conn = db.CreateConnection();
            var checkInRequired = await SeedEligibility.IsCheckInRequiredAsync(conn, req.TournamentId);
            var eligibleStatuses = SeedEligibility.ResolveStatuses(checkInRequired);

            var teamIds = req.Teams.Select(t => t.Id).ToArray();
            // Bracket slots use team_id for teams and participant id for solos
            var ineligibleTeams = await conn.QueryAsync<Guid>(
                """
                SELECT COALESCE(tp.team_id, tp.id) AS slot_id
                FROM tournament_participants tp
                WHERE tp.tournament_id = @tournamentId
                  AND (tp.team_id = ANY(@teamIds) OR tp.id = ANY(@teamIds))
                  AND tp.status::text != ALL(@eligibleStatuses)
                """,
                new { tournamentId = req.TournamentId, teamIds, eligibleStatuses });

            var ineligibleList = ineligibleTeams.ToList();
            if (ineligibleList.Count > 0)
            {
                return Results.BadRequest(new
                {
                    error = checkInRequired
                        ? "Some teams have not checked in. Only checked-in teams can be seeded when check-in is required."
                        : "Some teams are not eligible for seeding. Teams must be approved or checked in.",
                    ineligibleTeamIds = ineligibleList
                });
            }
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build d:\esportra-backend`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/Esportra.Api/Endpoints/BracketEndpoints.cs
git commit -m "$(cat <<'EOF'
feat: validate team eligibility in bracket generation endpoint

Server-side check ensures only checked-in teams are seeded when
check_in_required is true, and approved+checked_in when false.
EOF
)"
```

---

### Task 3: Guard check-in and remove-unchecked endpoints

**Files:**
- Modify: `src/Esportra.Api/Endpoints/TournamentEndpoints.cs:1703-1727` (check-in endpoint)
- Modify: `src/Esportra.Api/Endpoints/TournamentEndpoints.cs:1866-1894` (remove-unchecked endpoint)

**Interfaces:**
- Consumes: `IDbConnection`, `tournaments.check_in_required` column
- Produces: 400 response when check-in is disabled for the tournament

- [ ] **Step 1: Guard the check-in endpoint**

In `src/Esportra.Api/Endpoints/TournamentEndpoints.cs`, in the `POST /api/tournaments/{id}/check-in` handler (line 1703), after `using var conn = db.CreateConnection();` (line 1712), insert:

```csharp
            var checkInEnabled = await conn.ExecuteScalarAsync<bool>(
                "SELECT COALESCE(check_in_required, false) FROM tournaments WHERE id = @id",
                new { id });
            if (!checkInEnabled)
                return Results.BadRequest(new { error = "Check-in is not enabled for this tournament." });
```

- [ ] **Step 2: Guard the remove-unchecked endpoint**

In the `POST /api/tournaments/{id}/remove-unchecked` handler (line 1866), after the ownership check (`if (!isOwner) return Results.Forbid();` at line 1881), insert:

```csharp
            var checkInEnabled = await conn.ExecuteScalarAsync<bool>(
                "SELECT COALESCE(check_in_required, false) FROM tournaments WHERE id = @id",
                new { id });
            if (!checkInEnabled)
                return Results.BadRequest(new { error = "Check-in is not enabled for this tournament." });
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build d:\esportra-backend`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/Esportra.Api/Endpoints/TournamentEndpoints.cs
git commit -m "$(cat <<'EOF'
feat: guard check-in and remove-unchecked endpoints

Returns 400 when check_in_required is false, preventing actions that
don't apply when check-in is disabled.
EOF
)"
```

---

### Task 4: Conditional mock participant status

**Files:**
- Modify: `src/Esportra.Api/Endpoints/TournamentEndpoints.cs:4288-4361` (mock/generate endpoint)

**Interfaces:**
- Consumes: `tournaments.check_in_required` column (already fetched in tournament query at line 4288)
- Produces: Mock participants with status `approved` when check-in disabled, `checked_in` when enabled

- [ ] **Step 1: Fetch `check_in_required` in the tournament query**

In the mock/generate endpoint (line 4288), modify the SELECT to include `check_in_required`:

```sql
SELECT organizer_id, status, is_public, max_teams, team_size, game, format, check_in_required FROM tournaments WHERE id = @id AND deleted_at IS NULL FOR UPDATE
```

- [ ] **Step 2: Use conditional status for mock participants**

At line 4348, change:

```csharp
mockStatus = "checked_in",
```

To:

```csharp
mockStatus = (bool)(tournament.check_in_required ?? false) ? "checked_in" : "approved",
```

- [ ] **Step 3: Conditionally set `checked_in_at`**

At line 4356, the INSERT currently always sets `checked_in_at` to `NOW()`. Change the SQL to conditionally set it:

```sql
INSERT INTO tournament_participants
    (id, tournament_id, team_id, team_name, participant_type, status, is_mock, checked_in_at, created_at, updated_at)
VALUES
    (@mockId, @tournamentId, @mockId, @teamName, @participantType::registration_type,
     @mockStatus::registration_status, TRUE, CASE WHEN @mockStatus = 'checked_in' THEN NOW() ELSE NULL END, NOW(), NOW())
```

- [ ] **Step 4: Build and verify**

Run: `dotnet build d:\esportra-backend`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/Esportra.Api/Endpoints/TournamentEndpoints.cs
git commit -m "$(cat <<'EOF'
feat: generate mock participants with status based on check-in setting

When check_in_required is false, mocks are created as 'approved' instead
of 'checked_in' to reflect actual tournament behavior.
EOF
)"
```

---

### Task 5: Frontend — Replace mandatory check-in with toggle

**Files:**
- Modify: `D:\frag-and-book-main\src\components\tournament\wizard\StepRegistration.tsx:55-169`
- Modify: `D:\frag-and-book-main\src\types\tournamentWizard.ts:165`

**Interfaces:**
- Consumes: `data.checkInRequired` (boolean from wizard state)
- Produces: Toggle UI that sets `checkInRequired` true/false, hides sub-settings when false

- [ ] **Step 1: Change default in `tournamentWizard.ts`**

In `D:\frag-and-book-main\src\types\tournamentWizard.ts`, at line 165, change:

```typescript
checkInRequired: true,
```

To:

```typescript
checkInRequired: false,
```

- [ ] **Step 2: Remove forced check-in enforcement in `StepRegistration.tsx`**

In `D:\frag-and-book-main\src\components\tournament\wizard\StepRegistration.tsx`, remove lines 64-70 (the `useEffect` body that forces `checkInRequired: true` and `autoRemoveUnchecked: true`):

```typescript
        // Enforce mandatory check-in fields
        if (!data.checkInRequired) {
            updates.checkInRequired = true;
        }
        if (!data.autoRemoveUnchecked) {
            updates.autoRemoveUnchecked = true;
        }
```

Remove those 6 lines entirely. Also remove `data.checkInRequired` and `data.autoRemoveUnchecked` from the `useEffect` dependency array on line 75.

- [ ] **Step 3: Replace "Mandatory" badge with a Switch toggle**

Replace the check-in settings section (lines 116-169) with:

```tsx
            {/* Check-in Settings - Optional */}
            <div className="w-full h-px bg-white/5 my-6" />
            <div className="p-4 bg-white/[0.02] rounded-lg border border-white/10 space-y-4">
                <div className="flex items-center gap-3">
                    <UserCheck className="w-5 h-5 text-emerald-400" />
                    <div>
                        <div className="font-medium text-white">Require Check-In</div>
                        <div className="text-sm text-gray-400">
                            Teams must confirm attendance before the tournament starts
                        </div>
                    </div>
                    <Switch
                        checked={data.checkInRequired}
                        onCheckedChange={(checked) => updateData({
                            checkInRequired: checked,
                            autoRemoveUnchecked: checked ? (data.autoRemoveUnchecked ?? true) : false,
                        })}
                        className="ml-auto"
                    />
                </div>

                {data.checkInRequired && (
                    <div className="space-y-4 pt-4 border-t border-white/10">
                        <div className="space-y-2">
                            <Label htmlFor="checkInWindow" className="flex items-center gap-2 text-sm text-xs font-bold text-gray-500 uppercase tracking-widest">
                                <Clock className="w-4 h-4" />
                                Check-in window (minutes before start)
                            </Label>
                            <div className="flex items-center gap-3">
                                <Input
                                    id="checkInWindow"
                                    type="number"
                                    min={5}
                                    max={120}
                                    value={data.checkInWindowMinutes}
                                    onChange={(e) => updateData({ checkInWindowMinutes: parseInt(e.target.value) || 30 })}
                                    className="w-24 font-bold tracking-tight"
                                />
                                <span className="text-sm text-gray-400">minutes</span>
                            </div>
                            <p className="text-xs text-gray-500">
                                Teams can check in starting {data.checkInWindowMinutes} minutes before the tournament starts
                            </p>
                        </div>

                        <div className="flex items-center justify-between">
                            <div>
                                <div className="font-medium text-white text-sm">Auto-remove no-shows</div>
                                <div className="text-xs text-gray-400">
                                    Automatically remove teams who don't check in
                                </div>
                            </div>
                            <Switch
                                checked={data.autoRemoveUnchecked}
                                onCheckedChange={(checked) => updateData({ autoRemoveUnchecked: checked })}
                            />
                        </div>
                    </div>
                )}
            </div>
```

- [ ] **Step 4: Ensure `Switch` import exists**

Check for existing `Switch` import in the file. If not present, add:

```typescript
import { Switch } from '@/components/ui/switch';
```

- [ ] **Step 5: Verify the page renders without errors**

Run: `cd D:\frag-and-book-main && npm run dev`
Navigate to the tournament creation wizard step 4 (Registration). Verify the toggle appears and hides/shows the check-in sub-settings.

- [ ] **Step 6: Commit**

```bash
cd D:\frag-and-book-main
git add src/components/tournament/wizard/StepRegistration.tsx src/types/tournamentWizard.ts
git commit -m "$(cat <<'EOF'
feat: replace mandatory check-in with optional toggle

Organizers can now enable or disable tournament check-in. When disabled,
check-in window and auto-remove settings are hidden.
EOF
)"
```

---

### Task 6: Frontend — Conditional check-in UI in organizer dashboard

**Files:**
- Modify: `D:\frag-and-book-main\src\pages\organizer\TournamentManage.tsx:1905-2009` (check-in monitor)
- Modify: `D:\frag-and-book-main\src\pages\organizer\TournamentManage.tsx:2411-2450` (settings tab)

**Interfaces:**
- Consumes: `effectiveCheckInRequired` (already computed at line 999 from `tournament.check_in_required`)
- Produces: Check-in monitor and settings card only rendered when check-in is enabled

- [ ] **Step 1: Wrap check-in monitor with check-in enabled condition**

At line 1905, `showCheckInSummary` is already computed as:
```typescript
const showCheckInSummary = Boolean(
    (effectiveCheckInRequired || canActAsOwner) && checkInEligibleParticipants.length > 0,
);
```

Change this (line 1036-1038) to only show when check-in is enabled:

```typescript
const showCheckInSummary = Boolean(
    effectiveCheckInRequired && checkInEligibleParticipants.length > 0,
);
```

This removes the check-in monitor entirely when check-in is disabled.

- [ ] **Step 2: Make settings tab conditional**

Replace the settings tab check-in card (lines 2418-2450) with:

```tsx
                      <Card className="relative bg-[#0d0d10] border border-white/10 rounded-none overflow-hidden p-6 sm:p-8 mb-6 group">
                        <CardHeader className="p-0 pb-4 border-b border-white/5 mb-4">
                          <CardTitle className="text-lg font-semibold text-white">Check-In Requirements</CardTitle>
                        </CardHeader>
                        <CardContent className="p-0 space-y-5">
                          {effectiveCheckInRequired ? (
                            <div className="flex flex-col gap-4">
                              <div className="flex items-start gap-2 text-sm text-gray-300 p-4 rounded-none bg-white/[0.02] border border-white/5">
                                <AlertTriangle className="w-5 h-5 text-rose-400 mt-0.5 flex-shrink-0" />
                                <div>
                                  <p className="font-medium text-white mb-1">Check-in Enforcement</p>
                                  <ul className="list-disc list-inside space-y-1 text-gray-400">
                                    <li>Check-in is <span className="text-rose-400 font-medium">required</span> for all teams.</li>
                                    <li>Teams who fail to check in before the deadline will be <span className="text-red-400 font-medium">auto-removed</span>.</li>
                                    <li>Only checked-in teams will be added to the bracket.</li>
                                  </ul>
                                </div>
                              </div>
                              <div className="flex flex-col sm:flex-row items-center justify-between p-4 rounded-none bg-white/[0.02] border border-white/5 gap-4">
                                <div>
                                  <p className="font-semibold text-white">Manual Enforcement</p>
                                  <p className="text-sm text-gray-400">You can manually trigger removal of teams who haven't checked in yet.</p>
                                </div>
                                <DangerButton
                                  onClick={handleRemoveUncheckedParticipants}
                                  disabled={removingUnchecked}
                                  className="w-full sm:w-auto min-w-[200px]"
                                >
                                  {removingUnchecked ? 'Processing...' : 'Remove Unchecked Teams'}
                                </DangerButton>
                              </div>
                            </div>
                          ) : (
                            <div className="flex items-start gap-2 text-sm text-gray-300 p-4 rounded-none bg-white/[0.02] border border-white/5">
                              <div>
                                <p className="font-medium text-white mb-1">Check-in Disabled</p>
                                <p className="text-gray-400">
                                  Check-in is not required for this tournament. All approved teams are eligible for bracket seeding.
                                </p>
                              </div>
                            </div>
                          )}
                        </CardContent>
                      </Card>
```

- [ ] **Step 3: Verify the dashboard renders correctly**

Navigate to organizer dashboard for a tournament with `check_in_required = false`. Verify:
- No check-in monitor card on participants tab
- Settings tab shows "Check-in Disabled" messaging
- No "Remove Unchecked Teams" button visible

- [ ] **Step 4: Commit**

```bash
cd D:\frag-and-book-main
git add src/pages/organizer/TournamentManage.tsx
git commit -m "$(cat <<'EOF'
feat: conditionally render check-in UI based on tournament setting

Hide check-in monitor and enforcement controls when check-in is
disabled. Show informational message instead.
EOF
)"
```

---

### Task 7: Frontend — Conditional stage setup and bracket generation

**Files:**
- Modify: `D:\frag-and-book-main\src\components\organizer\wizard\StageSetupWizard.tsx:719-740`
- Modify: `D:\frag-and-book-main\src\components\organizer\tabs\StageManagementTab.tsx:370-413`

**Interfaces:**
- Consumes: `checkInEnabled` (state from tournament API in StageSetupWizard), `stage.config.use_check_in_only` (stage setting)
- Produces: `use_check_in_only` checkbox hidden when check-in disabled; bracket generation fetches all eligible participants when disabled

- [ ] **Step 1: Stage setup wizard already correct**

The `StageSetupWizard.tsx` already gates the `use_check_in_only` checkbox behind `checkInEnabled` (line 719: `{currentStageIndex === 0 && checkInEnabled && (`). This reads from the tournament API response's `check_in_required` field (line 261). No change needed here.

- [ ] **Step 2: Add `checkInRequired` prop to `StageManagementTab`**

In `D:\frag-and-book-main\src\components\organizer\tabs\StageManagementTab.tsx`, update the props interface (line 23-29):

```typescript
interface StageManagementTabProps {
    tournamentId: string;
    stages: DashboardStage[];
    onUpdate: () => void;
    game: string;
    isPublic?: boolean;
    checkInRequired?: boolean;
}
```

Update the component destructuring (line 31):

```typescript
export const StageManagementTab: React.FC<StageManagementTabProps> = ({ tournamentId, stages, onUpdate, game, isPublic = false, checkInRequired = false }) => {
```

- [ ] **Step 3: Pass the prop from `TournamentManage.tsx`**

In `D:\frag-and-book-main\src\pages\organizer\TournamentManage.tsx` (line 1792-1798), add the prop:

```tsx
                      <StageManagementTab
                        tournamentId={tournament.id}
                        stages={stages}
                        onUpdate={() => refetchDashboard()}
                        game={tournament.game || ''}
                        isPublic={tournament.is_public}
                        checkInRequired={!!tournament.check_in_required}
                      />
```

- [ ] **Step 4: Update bracket generation logic to use the prop**

In `D:\frag-and-book-main\src\components\organizer\tabs\StageManagementTab.tsx` at line 374, change:

```typescript
const useCheckInOnly = stage.stage_order === 1 && !!stageConf.use_check_in_only;
```

To:

```typescript
const useCheckInOnly = stage.stage_order === 1 && checkInRequired && !!stageConf.use_check_in_only;
```

- [ ] **Step 3: Verify bracket generation works with check-in disabled**

1. Create a tournament with check-in disabled
2. Add some approved participants
3. Go to Stage Management → Generate Bracket
4. Verify all approved participants are included without check-in filtering

- [ ] **Step 5: Commit**

```bash
cd D:\frag-and-book-main
git add src/components/organizer/tabs/StageManagementTab.tsx src/pages/organizer/TournamentManage.tsx
git commit -m "$(cat <<'EOF'
feat: skip check-in filtering in bracket generation when disabled

Pass checkInRequired prop to StageManagementTab. When tournament
check_in_required is false, ignore use_check_in_only stage setting
and include all approved participants.
EOF
)"
```

---

### Task 8: Frontend — Hide participant-facing check-in UI when disabled

**Files:**
- Modify: `D:\frag-and-book-main\src\pages\tournaments\Details.tsx:183-193`

**Interfaces:**
- Consumes: `requiresCheckIn` (already computed at line 121 from `tournament.check_in_required`)
- Produces: `canSelfCheckIn` is `false` when check-in disabled (already the case since line 183 starts with `requiresCheckIn &&`)

- [ ] **Step 1: Verify existing logic**

The `canSelfCheckIn` computation at line 183 already starts with `requiresCheckIn &&`:

```typescript
const canSelfCheckIn =
    requiresCheckIn &&
    !!registrationDetails &&
    ...
```

When `check_in_required = false`, `requiresCheckIn` is `false`, so `canSelfCheckIn` is already `false`. The "CONFIRM PRESENCE" button and countdown timer are already gated behind this. **No code change needed.**

- [ ] **Step 2: Verify in browser**

Navigate to a tournament detail page where check-in is disabled. Verify:
- No "CONFIRM PRESENCE" button visible
- No check-in countdown timer
- Registration flow works normally (approve → ready for seeding)

- [ ] **Step 3: Commit (skip if no changes)**

No commit needed — existing logic already handles this correctly.

---

### Task 9: Frontend — Validation schema update

**Files:**
- Modify: `D:\frag-and-book-main\src\schemas\tournamentSchema.ts:90-92`

**Interfaces:**
- Consumes: `checkInRequired` boolean in form data
- Produces: `checkInWindowMinutes` validation only enforced when `checkInRequired` is true (already the case at lines 103-109)

- [ ] **Step 1: Verify existing schema**

The validation at lines 101-109 already conditionally validates:

```typescript
export const registrationSchema = registrationSchemaBase
    .refine(
        (data) => {
            if (data.checkInRequired) {
                return data.checkInWindowMinutes >= 5 && data.checkInWindowMinutes <= 120;
            }
            return true;
        },
        { message: 'Check-in window must be between 5 and 120 minutes', path: ['checkInWindowMinutes'] },
    )
```

This already only validates window minutes when `checkInRequired` is true. **No code change needed.**

- [ ] **Step 2: Verify form submission works**

1. Create a tournament with check-in disabled
2. Submit the form
3. Verify no validation errors about check-in window

- [ ] **Step 3: Commit (skip if no changes)**

No commit needed — existing schema already handles this correctly.

---

## Summary

| Task | Scope | Changes |
|------|-------|---------|
| 1 | Backend | Create `SeedEligibility`, refactor `BrSeedEligibility` |
| 2 | Backend | Validate team eligibility in bracket generation |
| 3 | Backend | Guard check-in + remove-unchecked endpoints |
| 4 | Backend | Conditional mock participant status |
| 5 | Frontend | Toggle in wizard, change default to disabled |
| 6 | Frontend | Conditional dashboard check-in UI |
| 7 | Frontend | Conditional stage setup + bracket generation |
| 8 | Frontend | Verify participant-facing UI (no change needed) |
| 9 | Frontend | Verify validation schema (no change needed) |
