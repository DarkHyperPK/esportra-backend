# Bracket System — Full Audit
**Date:** 2026-03-20  
**Scope:** All frontend bracket code in `D:\frag-and-book-main\src`  
**Method:** Line-by-line read of every file. No inference — only what the code actually does.

---

## Files Audited

| File | Purpose |
|------|---------|
| `services/bracket/SingleEliminationGenerator.ts` | SE bracket generation |
| `services/bracket/DoubleEliminationGenerator.ts` | DE bracket generation |
| `services/bracket/RoundRobinGenerator.ts` | Round-robin / group stage generation |
| `services/bracket/SwissGenerator.ts` | Swiss R1 generation + next-round delegation |
| `services/bracket/BracketGenerator.ts` | IBracketGenerator interface + GraphValidator |
| `services/bracket/BracketAdapter.ts` | BracketNode[] → BracketMatch[] conversion |
| `services/bracket/MatchRepository.ts` | DB fetch/persist via .NET API |
| `services/bracket/GraphMatchService.ts` | Score save, go-live, clear |
| `services/bracket/AdvancementService.ts` | Bye advancement, reset |
| `services/bracket/optimisticBracket.ts` | Optimistic UI state patches |
| `services/bracket/StandingsService.ts` | Standings fetch |
| `components/bracket/BracketRenderer.tsx` | Position calculation + card layout |
| `components/bracket/ReadOnlyMatchCard.tsx` | Individual match card (public) |
| `components/bracket/BracketSidebarFilter.tsx` | Round/section filter sidebar |
| `components/bracket/SwissView.tsx` | Swiss bracket render (organizer + public) |
| `components/bracket/GroupStageView.tsx` | Round-robin group render |
| `hooks/useGraphBracket.ts` | TanStack Query wrapper for bracket graph |
| `hooks/useBracketRealtime.ts` | SignalR real-time bracket updates |
| `hooks/usePublicBracketData.ts` | Stages + bracket versions for public view |
| `pages/tournaments/brackets/PublicBracketView.tsx` | Public bracket page orchestrator |
| `types/bracket-graph.ts` | BracketVersion, BracketNode, BracketEdge types |
| `types/bracketTypes.ts` | BracketMatch, BracketTeam, BracketSide types |
| `constants/bracketConstants.ts` | Old position constants (partially orphaned) |

---

## Part 1 — Single Elimination Generator

**File:** `SingleEliminationGenerator.ts`

### Round count formula

```ts
const fullRounds = Math.log2(powerOfTwo);
const effectiveAdvCount = advancementCount && advancementCount > 0 ? advancementCount : 1;
const targetRemainingTeams = Math.max(1, Math.pow(2, Math.ceil(Math.log2(effectiveAdvCount))));
const numRounds = Math.max(1, fullRounds - Math.log2(targetRemainingTeams));
```

**Verification for 8 teams, advancementCount=1:**  
→ powerOfTwo=8, fullRounds=3, targetRemainingTeams=1, numRounds=3 ✅  

**Verification for 8 teams, advancementCount=4:**  
→ targetRemainingTeams=4, numRounds=3-2=1 ✅ (one qualifying round, top 4 advance)

**Verification for 5 teams:**  
→ powerOfTwo=8, numRounds=3. Creates 4 R1 matches with 3 byes. **Correct** — byes are manual per comment.

### Seeding algorithm

`getStandardBracketSlots(8)` trace:
- n=2 → [0,1]  
- n=4 → upperSlots=[0,1] → slots[0]=0, slots[3]=1, slots[1]=2, slots[2]=3 → **[0,2,3,1]**  
- n=8 → upperSlots=[0,2,3,1] → [0,4,6,2,3,7,5,1]

Match pairing (team[i*2] vs team[i*2+1]):
- M1: slot0(Seed1) vs slot1(Seed8) → **1v8** ✅  
- M2: slot2(Seed4) vs slot3(Seed5) → **4v5** ✅  
- M3: slot4(Seed2) vs slot5(Seed7) → **2v7** ✅  
- M4: slot6(Seed3) vs slot7(Seed6) → **3v6** ✅  

**Seeding is correct for all power-of-2 sizes.**

For non-power-of-2 (e.g. 5 teams in 8-slot bracket): seeds 1-5 placed at slots 0,4,6,2,3. Slots 1,5,7 = null (bye). Results in matches: Seed1vBYE, Seed4vSeed5, Seed2vBYE, Seed3vBYE. 3 byes in R1 is correct for 5-team SE.

### Winner advancement edges

```ts
const nextMatchNum = Math.ceil((i + 1) / 2);
target_slot: (i + 1) % 2 === 1 ? 1 : 2
```

For 4 matches in R1: M1→R2M1(slot1), M2→R2M1(slot2), M3→R2M2(slot1), M4→R2M2(slot2) ✅

### ✅ VERDICT: SingleEliminationGenerator is mathematically correct and scalable.

---

## Part 2 — Double Elimination Generator

**File:** `DoubleEliminationGenerator.ts`

### LB node count formula

```ts
const numLowerRounds = 2 * numUpperRounds - 2;
const matchesInRound = Math.pow(2, Math.floor((numLowerRounds - 1 - r) / 2));
```

**Verification for 8 teams** (numUpperRounds=3, numLowerRounds=4):
| LB r | formula | matches |
|------|---------|---------|
| 0 | floor(3/2)=1 → 2^1 | **2** |
| 1 | floor(2/2)=1 → 2^1 | **2** |
| 2 | floor(1/2)=0 → 2^0 | **1** |
| 3 | floor(0/2)=0 → 2^0 | **1** |

Total = 6 LB matches. For 8-team DE: 7 eliminations total, 1 is GF → 6 LB matches. ✅

**Verification for 16 teams** (numUpperRounds=4, numLowerRounds=6):
| LB r | formula | matches |
|------|---------|---------|
| 0 | floor(5/2)=2 → 2^2 | **4** |
| 1 | floor(4/2)=2 → 2^2 | **4** |
| 2 | floor(3/2)=1 → 2^1 | **2** |
| 3 | floor(2/2)=1 → 2^1 | **2** |
| 4 | floor(1/2)=0 → 2^0 | **1** |
| 5 | floor(0/2)=0 → 2^0 | **1** |

Total = 14. For 16-team DE: 15 eliminations, 1 is GF → 14 LB matches. ✅

### Loser drop formula (WB → LB)

```ts
const lr = r === 0 ? 0 : (2 * r - 1);
const dropMatchNum = (r === 0) ? Math.ceil((i + 1) / 2) : (i + 1);
```

**Verification for 8 teams:**
- r=0 (WB R1, 4 matches): lr=0 (LB R0). 4 losers → 2 LB R0 matches (2 per match via ceil). ✅  
- r=1 (WB R2, 2 matches): lr=1 (LB R1). 2 losers → LB R1 M1 and M2 as slot 1. ✅  
- WB Final (r=2): handled separately → LB Final (r=3). ✅

**Verification for 16 teams:**
- r=0 (WB R1, 8 matches): lr=0. 8 losers → 4 LB R0 matches (2 per match). ✅  
- r=1 (WB R2, 4 matches): lr=1. 4 losers → LB R1 M1,M2,M3,M4 as slot 1. ✅  
- r=2 (WB R3, 2 matches): lr=3. 2 losers → LB R3 M1,M2 as slot 1. ✅  
- WB Final (r=3): handled separately → LB R5 (LB Final). ✅

**The loser drop formula IS correct.** LB rounds are 0-indexed; lr correctly identifies which LB round receives each WB round's losers.

### LB internal edges

```ts
const nextMatchNum = (r % 2 === 0) ? (i + 1) : Math.ceil((i + 1) / 2);
const slot = (r % 2 === 0) ? 2 : ((i + 1) % 2 === 1 ? 1 : 2);
```

For 8-team DE:
- LB R0 (even, 2 matches): M1→LB R1 M1(slot2), M2→LB R1 M2(slot2). WB R2 losers go to slot1. ✅  
- LB R1 (odd, 2 matches): M1→LB R2 M1(slot1), M2→LB R2 M1(slot2). ✅  
- LB R2 (even, 1 match): M1→LB R3 M1(slot2). WB Final loser goes to slot1. ✅

### ✅ VERDICT: DoubleEliminationGenerator graph structure (nodes, edges, slot assignments) is mathematically correct and scalable to N=2^k teams.

---

## Part 3 — BracketRenderer Position Algorithm

**File:** `BracketRenderer.tsx`

### WB slot formula

```ts
const power = Math.pow(2, rIdx);
const slot = idx * power + (power - 1) / 2;
const y = headingHeight + headingMargin + (slot * (cardHeight + matchGap));
// defaults: headingHeight=40, headingMargin=40, cardHeight=86, matchGap=30
// → unit = 116px per slot
```

**Verification for 8-team SE (rIdx=0,1,2):**

rIdx=0 (4 matches): slots 0,1,2,3 → y = 80, 196, 312, 428  
rIdx=1 (2 matches): slots 0.5, 2.5 → y = 138, 370  
rIdx=2 (1 match): slot 1.5 → y = 254

**Centering check rIdx=1, M0:** Center of R1M0 and R1M1 = (80+43 + 196+43) / 2 = 181. R2M0 center = 138+43 = **181** ✅  
**Centering check rIdx=1, M1:** Center of R1M2 and R1M3 = (312+43 + 428+43) / 2 = 413. R2M1 center = 370+43 = **413** ✅  
**Centering check rIdx=2:** Center of R2M0 and R2M1 = (138+43 + 370+43) / 2 = 297. R3M0 center = 254+43 = **297** ✅

**The slot formula is mathematically correct.** Fractional slots are intentional and produce perfect centering for all power-of-2 and non-power-of-2 bracket sizes.

### 🔴 BUG 1 — LB uses flat linear spacing, not slot-based

```ts
lRounds.forEach((round, rIdx) => {
    rounds.losers[round].forEach((m, idx) => {
        const y = losersStartY + (idx * (cardHeight + matchGap));  // linear
    });
});
```

For 8-team DE (LB matches per round: 2, 2, 1, 1):
- LB R0: M0@losersStartY, M1@losersStartY+116
- LB R1: M0@losersStartY, M1@losersStartY+116  ← same as R0, not centered between anything
- LB R2: M0@losersStartY  ← should be at (LB_R1_M0_center + LB_R1_M1_center)/2 = losersStartY+58
- LB R3: M0@losersStartY  ← same as R2

**Visual result:** LB R2 (1 match) sits at the same Y as LB R1 M0 instead of being centered between both R1 matches. The LB bracket looks like a left-aligned column instead of a pyramid tree. This affects all double elimination brackets regardless of team count.

**Fix needed:** Apply the same slot-based centering algorithm to LB rounds that is applied to WB rounds.

### 🔴 BUG 2 — GF and LB Final share the same X column

```ts
// GF X:
const finalX = leftPadding + (wRounds.length * (cardWidth + roundGap));

// LB matches X (inside lRounds.forEach):
const x = leftPadding + (rIdx * (cardWidth + roundGap));
```

For 8-team DE: wRounds.length=3, lRounds length=4 (rIdx 0..3).

- GF X = leftPadding + 3 × 360 = **leftPadding + 1080**
- LB Final (rIdx=3) X = leftPadding + 3 × 360 = **leftPadding + 1080**

They are at the same X coordinate but different Y (GF at y≈254, LB Final at y≈674+). The GF card and LB Final card sit in the same column separated by ~420px of vertical space, making the GF appear to "hang" at the top-right with no visual relation to the LB section below.

**Expected behavior:** GF should be placed to the right of both WB Final and LB Final columns, at an X that is `max(wRounds.length, lRounds.length) × (cardWidth + roundGap)`.

### 🟡 DEAD CODE — `matchSlots` map is populated but never read

```ts
const matchSlots = new Map<string, number>();
// ...
matchSlots.set(id, slot);
matchSlots.set(rawId, slot);
```

`matchSlots` is created, written to for every WB match, but **never read anywhere** in the file. Dead code.

### Finals Y position

```ts
const lastWinnerMatch = rounds.winners[wRounds[wRounds.length - 1]]?.[0];
let finalY = 100;
if (lastWinnerMatch) {
    const p = map.get(String(lastWinnerMatch.id));
    if (p) finalY = p.y;
}
```

For SE: GF is placed at the same Y as the WB Final. Correct visually.  
For DE: GF at same Y as WB Final (top-right area) while LB Final is far below. Related to Bug 2 above — not a separate bug, same root cause.

### Filter X/Y offset computation

```ts
let minX = Infinity;
matches.forEach(m => { /* scan all matches */ });
return minX === Infinity ? 0 : minX - leftPadding;
```

Scans entire matches array on every filter change. For 64-team DE (~190 matches), this is O(n) per filter change — acceptable. Safe fallback when minX stays Infinity (empty filtered round). ✅

### ReadOnlyMatchCard dimensions match BracketRenderer defaults

Card CSS: `width: 260, height: 86` (35+35+16px rows).  
BracketRenderer defaults: `cardWidth=260, cardHeight=86`. ✅ Consistent.

---

## Part 4 — BracketAdapter

**File:** `BracketAdapter.ts`

### ID prefixing

All BracketMatch IDs are `db-${node.id}`. nextMatchId is `db-${winnerEdge.target_match_id}`.  
Both prefixed and raw IDs are stored in matchPositions map (redundant but safe).  
PublicBracketView strips prefix for API calls: `id.replace(/^(db-|wb-|lb-)/, '')`. ✅

### 🟡 BUG 3 — stageId set to version_id, not stage_id

```ts
stageId: node.version_id,  // line 106
```

`BracketNode` has `version_id` but **not** `stage_id` directly. So `BracketMatch.stageId` receives the version UUID, not the stage UUID. Any code that uses `match.stageId` expecting the real stage ID will get the wrong value.

**Confirmed impact:** SwissGenerator.generateNextRound and GroupStageView accept `stageId` as a param — these are called with explicit `stageId` from the parent component, not from `match.stageId`. So no functional breakage currently. But it is a semantic bug that will cause issues if match.stageId is ever relied upon.

### Seed values

```ts
team1: createBracketTeam(team1, node.match_number * 2 - 1),
team2: createBracketTeam(team2, node.match_number * 2),
```

Seeds are assigned as `matchNumber*2-1` and `matchNumber*2`, not based on actual seeding. For an 8-team bracket: Match 1 → seeds 1,2; Match 2 → seeds 3,4, etc. This is not the actual bracket seeding order (1v8, 4v5...). Only affects the displayed seed number badge in the card — no functional impact.

---

## Part 5 — MatchRepository

**File:** `MatchRepository.ts`

### 🟡 BUG 4 — `x` field not mapped in getGraphStructure

```ts
const nodes: BracketNode[] = rawNodes.map((m: any) => ({
    id: m.id,
    // ...
    y: m.y,
    // x: m.x   ← MISSING
}));
```

`BracketNode.x` is in the interface but not mapped from the API response. RoundRobinGenerator sets explicit `x` coordinates on nodes (line 89), stores them in DB, but they are silently dropped on retrieval.

**Impact:** GroupStageView and SwissView do not use x/y from BracketNode objects; they render via their own layout logic. BracketRenderer also recomputes all positions independently. So no visible breakage currently. But the field is lost.

---

## Part 6 — PublicBracketView

**File:** `PublicBracketView.tsx`

### 🔴 BUG 5 — cached_ui_state bypasses BracketAdapter with no validation

```ts
const matches = useMemo(() => {
    if (graphData?.version?.cached_ui_state) {
        return graphData.version.cached_ui_state;  // any[]
    }
    // ...
    return adaptGraphToBracketMatches(...);
}, [graphData, teamsData]);
```

Issues:
1. If cache exists but is stale (graph updated, cache not cleared), the wrong bracket is shown silently.
2. The cache is typed as `any[]` — no type safety on the returned data.
3. For Swiss and Group Stage brackets, the cached matches have `bracketType: 'swiss_round'` or `'group'`. But the format detection below reads from the returned matches:

```ts
if (matches.some(m => m.bracketType === 'group')) return 'round_robin';
if (matches.some(m => m.bracketType === 'swiss_round')) return 'swiss';
return 'elimination';
```

If `cached_ui_state` objects lack `bracketType` (set via BracketAdapter line 103: `bracketType: node.bracket_type`), format detection falls through to `'elimination'`, causing Swiss/RR tournaments to render with BracketRenderer instead of SwissView/GroupStageView. This would show no matches (BracketRenderer filters by `bracketSide` which would be `undefined` for cached Swiss matches).

### 🔴 BUG 6 — Version picker does not prioritize `active` status

**File:** `usePublicBracketData.ts`

```ts
const stageVersions = (versionsData || [])
    .filter(v => v.stage_id === stage.id)
    .sort((a, b) => new Date(b.created_at).getTime() - new Date(a.created_at).getTime());

if (stageVersions.length > 0) {
    vMap[stage.id] = stageVersions[0].id;  // most recent by date
}
```

The query fetches versions with `status=active,draft,completed`. Versions are sorted by `created_at` descending. If an organizer creates a new `draft` version after the `active` one, the draft is returned as `stageVersions[0]` and shown publicly. **Public viewers would see the draft bracket instead of the live one.**

**Fix:** Sort by status priority first: `active > draft > completed`, then by `created_at`.

### Round tab derivation

Tabs are built from `Object.keys(winnersRounds)` and `Object.keys(losersRounds)` which only contain rounds that currently have matches. For Swiss (rounds generated incrementally), tabs auto-appear as each round is created. ✅

### Format detection fallback

If `cached_ui_state` is absent and BracketAdapter runs, `bracketType` is always set from `node.bracket_type`. Format detection is reliable in the non-cached path. ✅

---

## Part 7 — Swiss Generator

**File:** `SwissGenerator.ts`

### Round 1 "Slide" pairing

```ts
const half = Math.ceil(groupTeams.length / 2);
const topHalf = groupTeams.slice(0, half);
const bottomHalf = groupTeams.slice(half);
for (let i = 0; i < topHalf.length; i++) {
    team1 = topHalf[i];  // Seed 1 vs Seed N/2+1
    team2 = bottomHalf[i];
}
```

For 8 teams: 1v5, 2v6, 3v7, 4v8 — standard Swiss slide pairing. ✅  
For 5 teams: 1v4, 2v5, 3vBYE — BYE node created with only `team1_id`. ✅ (handled at lines 72-85)

### 🟡 BUG 7 — Swiss BYE match shows 'TBD' not 'BYE' in public view

BYE match node has `status: 'pending'` and `team2_id: undefined`. In ReadOnlyMatchCard:

```ts
{match.team2?.name || (isCompleted ? 'BYE' : 'TBD')}
```

Since status is 'pending', `isCompleted=false`, so BYE slot shows **'TBD'** to viewers. Should show **'BYE'**.

### Subsequent rounds delegated to .NET API

`SwissGenerator.generateNextRound` → `POST /api/swiss/next-round`. The .NET API handles Buchholz-based re-pairing. Frontend cannot audit this logic. ✅ (delegation is correct pattern)

---

## Part 8 — Round Robin Generator

**File:** `RoundRobinGenerator.ts`

### Circle algorithm correctness

For 4 teams [A,B,C,D], tracing each round:
- Round 0: circle=[A,B,C,D] → pairs: A-D, B-C ✅  
- Rotate: [A, D, B, C]  
- Round 1: pairs: A-C, D-B ✅  
- Rotate: [A, C, D, B]  
- Round 2: pairs: A-B, C-D ✅  

3 rounds, each team plays exactly once per round, every team plays every other team exactly once. ✅

For 5 teams (odd): adds BYE placeholder → 6 participants → 5 rounds, each round 2 real matches + 1 bye match (skipped). Every real team plays every other real team exactly once. ✅

### Snake group distribution

For 8 teams, 2 groups: Group 0 gets T1,T4,T5,T8; Group 1 gets T2,T3,T6,T7. Even seed distribution. ✅

### 🟢 CODE QUALITY — `round.indexOf(matchup)` is O(n)

```ts
y: roundIdx * 150 + (round.indexOf(matchup) * 80)
```

`round.indexOf(matchup)` searches the array for the object reference. The outer `round.forEach(matchup => ...)` doesn't expose the index. For large groups this is O(n²). Should be refactored to pass the index directly.

### 🟢 CODE QUALITY — console.log left in production code

`RoundRobinGenerator.ts` line 178 and `SwissGenerator.ts` lines 22,25,26,46,47,57 contain `console.log` calls that run in production.

---

## Part 9 — optimisticBracket

**File:** `optimisticBracket.ts`

### 🟡 BUG 8 — Tie score declares team2 winner

```ts
const winnerId = team1Score > team2Score ? team1Id : team2Id;
const loserId = team1Score > team2Score ? team2Id : team1Id;
```

On a tie (team1Score === team2Score), the `>` fails, so **team2 is always declared winner**. In BO formats ties cannot occur, but BO1 with a server crash or manual 0-0 entry would incorrectly award team2. Should guard against ties or use `>=` only after confirming tie-break rules.

### `applyReset` sets scores to 0, not null

```ts
team1_score: 0,
team2_score: 0,
```

After a reset, scores display as `0` instead of `-` (the card shows `{match.team1_score ?? '-'}`). Since 0 is not null, it renders `0` not `-`. Minor display inconsistency.

---

## Part 10 — useBracketRealtime

**File:** `useBracketRealtime.ts`

### 🟢 CODE QUALITY — `tournamentId` parameter is dead

The hook signature accepts `tournamentId` but it is **never used** inside the hook body. Only `versionId` is used for joining/leaving the SignalR bracket group.

```ts
interface Options {
  versionId: string | null | undefined;
  enabled?: boolean;
  // tournamentId is not in Options but passed via useGraphBracket
}
```

Actually, `useGraphBracket` passes `tournamentId` to `useBracketRealtime` but looking at `useBracketRealtime`'s Options interface, it doesn't have a `tournamentId` field — it destructures `versionId, enabled, onMatchUpdated, onBracketReset` only. So `tournamentId` in `useGraphBracket` line 11 is passed but silently ignored by the hook. No functional impact.

### SignalR cleanup

`conn.off(...)` and `LeaveBracket` invocation are correctly called in the cleanup function. ✅  
Reconnect handler re-joins bracket group. ✅

---

## Part 11 — GraphValidator

**File:** `BracketGenerator.ts`

### DFS cycle detection

Recursive DFS. For 256-team SE: ~255 matches, max stack depth = log2(256) = 8. No stack overflow risk. ✅

### Champion node check

Correctly identifies the single sink node (Grand Final) as the match with no outgoing winner edge. ✅  
Correctly skips check for RR/Swiss (edges.length === 0). ✅

### Slot collision check

Detects if two edges target the same match+slot. Guards against double-filling a bracket slot. ✅

---

## Part 12 — bracketConstants.ts vs BracketRenderer

**File:** `constants/bracketConstants.ts`

```ts
export const calculateY = (roundIndex: number, matchIndex: number) => {
    const offset = (Math.pow(2, roundIndex) - 1) * (S / 2);
    return Math.pow(2, roundIndex) * S * matchIndex + offset;
};
```

This implements the same centering formula as BracketRenderer but with different constants (S=140 vs BracketRenderer's 116). **This file is only imported by `GraphBracket.tsx` and `BracketGeneratorUI.tsx`** (the organizer-side drag-and-drop editor), not by `BracketRenderer.tsx` (the public view). The two renderers use independent implementations with different card+gap sizes.

---

## Summary Table

### 🔴 Critical Bugs

| # | Location | Description | Impact |
|---|----------|-------------|--------|
| B1 | `BracketRenderer.tsx:102-111` | LB bracket uses flat linear spacing — single-match rounds misaligned, no pyramid shape | All DE brackets visually broken |
| B2 | `BracketRenderer.tsx:113-132` | GF and LB Final placed at same X column, GF Y anchored to WB Final not centered between WB and LB Finals | DE Grand Final layout wrong |
| B5 | `PublicBracketView.tsx:113-114` | `cached_ui_state` bypasses BracketAdapter entirely; stale cache shown with no fallback; Swiss/RR cached state renders as elimination | Silent wrong bracket for cached tournaments |
| B6 | `usePublicBracketData.ts:40-45` | Version picker sorts by `created_at` only; a new draft created after publish makes public see draft bracket | Public viewers see unpublished bracket |

### 🟡 Medium Bugs

| # | Location | Description | Impact |
|---|----------|-------------|--------|
| B3 | `BracketAdapter.ts:106` | `stageId: node.version_id` — stageId on BracketMatch is version UUID not stage UUID | Semantic bug; no current breakage but dangerous |
| B4 | `MatchRepository.ts:52-77` | `x` field missing from node mapping in getGraphStructure | RR explicit x positions lost (not currently used) |
| B7 | `ReadOnlyMatchCard.tsx:110` | Swiss BYE match shows 'TBD' not 'BYE' | Display inconsistency |
| B8 | `optimisticBracket.ts:21-22` | Tie score always awards team2 as winner | Wrong optimistic state on 0-0 or tied score |
| B9 | `optimisticBracket.ts:152-153` | Reset sets scores to `0` not `null`; card renders `0` not `-` | Minor display issue after reset |

### 🟢 Code Quality Issues

| # | Location | Description |
|---|----------|-------------|
| Q1 | `BracketRenderer.tsx:73,85-86` | `matchSlots` Map populated but never read anywhere — dead code |
| Q2 | `useBracketRealtime.ts` | `tournamentId` passed from `useGraphBracket` but ignored by hook (not in Options interface) |
| Q3 | `RoundRobinGenerator.ts:89` | `round.indexOf(matchup)` — O(n) search, already have index from forEach |
| Q4 | `BracketRenderer.tsx:91-92` | Each match stored twice (prefixed + raw ID) — 2x memory in matchPositions map |
| Q5 | `types/bracket-graph.ts:14` | `cached_ui_state?: any[]` should be `BracketMatch[]` |
| Q6 | `SwissGenerator.ts:22-57` | Multiple `console.log` calls in production code |
| Q7 | `RoundRobinGenerator.ts:178` | `console.log` in production code |
| Q8 | `BracketNode interface` | `version: number` field is required in type but NOT mapped in `getGraphStructure` (line 52-77 of MatchRepository.ts) |

---

## Confirmed NOT Bugs (prior reports were incorrect)

| Claim | Actual finding |
|-------|---------------|
| "Slot formula produces fractional coordinates — broken for non-power-of-2" | **FALSE.** Fractional slots (0.5, 1.5, 2.5) are intentional. Verified algebraically: each round's cards are centered exactly at the midpoint of their two source cards. ✅ |
| "Loser drop formula `2*r-1` is wrong" | **FALSE.** LB rounds are 0-indexed. Formula correctly sends WB R1→LB0, WB R2→LB1, WB R3→LB3. Verified for 8 and 16 team DE. ✅ |
| "Round Robin circle algorithm is broken" | **FALSE.** Circle algorithm is textbook-correct for even and odd team counts. ✅ |
| "Swiss slide pairing is wrong" | **FALSE.** Slide pairing (top-half vs bottom-half) is standard Swiss R1. ✅ |
| "SE seeding algorithm is wrong" | **FALSE.** Produces 1v8, 4v5, 2v7, 3v6 for 8 teams. Standard bracket seeding. ✅ |

---

## Scalability Assessment

| Format | 4 teams | 8 teams | 16 teams | 32 teams | 64 teams | 128 teams |
|--------|---------|---------|----------|----------|----------|-----------|
| SE WB layout | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| SE GF position | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| DE WB layout | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| DE LB layout | ⚠️ flat | ⚠️ flat | ⚠️ flat | ⚠️ flat | ⚠️ flat | ⚠️ flat |
| DE GF position | ⚠️ overlaps LB col | ⚠️ overlaps | ⚠️ overlaps | ⚠️ overlaps | ⚠️ overlaps | ⚠️ overlaps |
| Round Robin | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Swiss R1 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Swiss subsequent rounds | Backend | Backend | Backend | Backend | Backend | Backend |

**DOM performance:** No virtualization. For 64-team DE (~190 cards), all DOM nodes rendered simultaneously. For 128-team (~380 cards), rendering lag expected. Not a correctness issue but a UX issue for large brackets.

---

## Priority Fix Order (Frontend)

1. **B6** — Fix version picker to prioritize `active` status (1-line sort change, high user impact)
2. **B1** — Apply slot-based centering to LB bracket in BracketRenderer (visual correctness for all DE users)
3. **B2** — Fix GF X position for DE (place GF to the right of both WB and LB columns)
4. **B5** — Add validation for cached_ui_state (check bracketType, add fallback to live BracketAdapter)
5. **B8** — Fix tie score handling in optimisticBracket
6. **B3** — Fix stageId in BracketAdapter (needs BracketNode to carry stage_id)
7. **Q1** — Remove dead `matchSlots` map
8. **Q5** — Type `cached_ui_state` as `BracketMatch[]`
9. **Q6/Q7** — Remove production console.logs

---

---

---

# PART III — MatchSystemEndpoints.cs Full Audit

**File:** `D:\esportra-backend\src\Esportra.Api\Endpoints\MatchSystemEndpoints.cs`  
**Purpose:** Normal player-facing match flow. Reports, acceptance (auto-finalization), disputes, check-ins, scheduling, time proposals.

---

## Part 24 — Report Submission (`POST /api/matches/{id}/reports`)

### Authorization
```csharp
var isCaptain = await conn.QuerySingleOrDefaultAsync<bool>(
    "SELECT EXISTS(... WHERE team_id = @teamId AND user_id = @userId AND role = 'captain' AND is_active = TRUE)");
if (!isCaptain) return Results.Forbid();
```
Only active captains of the reporting team can submit. ✅

### Dispute guard
```csharp
// Block submission if same game is already disputed
SELECT EXISTS(... WHERE match_id = @matchId AND game_number = @gameNumber AND status = 'disputed')
```
Prevents score re-submission while a dispute is open for that game. ✅

### Winner derivation
```csharp
if (derivedWinner is null && req.Team1Score != req.Team2Score)
{
    derivedWinner = req.Team1Score > req.Team2Score ? (Guid)matchTeams.team1_id : (Guid)matchTeams.team2_id;
}
```
If `WinnerTeamId` is null but scores differ, server derives winner from scores. ✅  
If scores are tied AND no explicit winner → `derivedWinner` stays null, stored with `winner_team_id = null`. This is safe — tied-score reports can exist. The downstream `/accept` endpoint handles the null case with a log warning (won't auto-process). ✅

### Upsert conflict handling
```sql
ON CONFLICT (match_id, game_number, reported_by_team_id)
DO UPDATE SET ... status = 'pending'
```
Re-submission by the same team for the same game replaces the previous report and resets to `pending`. This allows score correction before the opponent accepts. ✅

### 🟡 MS1 (Medium): No check that reporting team is actually IN the match

```csharp
// Only checks: is caller a captain of reportedByTeamId
var isCaptain = await conn.QuerySingleOrDefaultAsync<bool>(
    "... WHERE team_id = @teamId AND user_id = @userId AND role = 'captain'");
```
Does NOT verify that `reportedByTeamId` is `team1_id` or `team2_id` on the match. A captain of any team on the platform can report a result for any match, as long as they claim their team played it. Their team's report would be stored but would never auto-process (since `captainTeamId` check in `/accept` validates match membership), but the DB would have ghost reports for unrelated teams.

### Notification to opposing captain
Looks up opposing team by flipping team1/team2, fetches their captain, inserts notification, broadcasts `ReportSubmitted` via MatchHub. ✅

---

## Part 25 — Report Acceptance + Auto-Finalization (`POST /api/matches/{id}/reports/{rid}/accept`)

This is the **core match result flow**. Full analysis:

### Authorization
```csharp
var captainTeamId = await conn.QuerySingleOrDefaultAsync<Guid?>(
    "SELECT tm.team_id FROM team_members tm
     JOIN brkt_matches bm ON (bm.team1_id = tm.team_id OR bm.team2_id = tm.team_id)
     WHERE bm.id = @matchId AND tm.user_id = @userId AND tm.role = 'captain'");
if (captainTeamId is null) return Results.Forbid();
```
Validates: caller is an active captain of one of the two teams IN THIS SPECIFIC MATCH. ✅

### 🟡 MS2 (Medium): Accepting captain can accept their own team's report

The authorization check only verifies "is captain of a team in this match" — it does NOT verify "is captain of the team that did NOT submit the report." A captain could submit a report AND accept it themselves (no opponent verification).

In practice the frontend prevents this (the accept button is hidden from the reporting team), but there's no server-side check enforcing the opposing-team constraint.

**Correct fix:** Add `AND captainTeamId != (SELECT reported_by_team_id FROM match_result_reports WHERE id = @rid)`

### Series win tracking (BO1/BO3/BO5)
```csharp
int bestOf = Convert.ToInt32(match.best_of ?? 1);
int winsNeeded = (bestOf / 2) + 1; // BO1→1, BO3→2, BO5→3
```
BO1=1, BO3=2, BO5=3. ✅ Standard formula.

```csharp
// Count from brkt_match_games (persisted per-game results)
SELECT COUNT(*) FILTER (WHERE winner_id = @team1Id)::int AS team1_wins, ...
FROM brkt_match_games WHERE match_id = @matchId AND status = 'completed'
```
Series wins are counted from the `brkt_match_games` table, not from the report count. This is correct — it uses the authoritative game result record, not raw reports. ✅

### Finalization trigger
```csharp
if (team1Wins >= winsNeeded || team2Wins >= winsNeeded)
{
    seriesComplete = true;
    winnerId = team1Wins >= winsNeeded ? (Guid)match.team1_id : (Guid)match.team2_id;
    var finalized = await finalizer.FinalizeAsync(id, Convert.ToInt32(match.version), winnerId.Value, loserId, ...);
}
```
Only calls `FinalizeAsync` (bracket advancement) once `winsNeeded` threshold is reached. For BO1 this is on first acceptance. For BO3, only after team reaches 2 wins. ✅

### Version conflict handling
```csharp
catch (InvalidOperationException ex)
{
    logger.LogWarning(ex, "Match {MatchId} finalization version conflict", id);
}
```
Version conflicts are **logged but not returned as errors** — the `/accept` response still returns `success: true`. This means: if two people click accept simultaneously, the second one silently fails the finalization. The match report is marked accepted but bracket advancement doesn't happen.

### 🔴 MS3 (Critical): Version conflict in auto-process is silently swallowed

```csharp
catch (InvalidOperationException ex)
{
    logger.LogWarning(ex, "Match {MatchId} finalization version conflict", id);
}
// returns: { success: true, processed: false, seriesComplete: false }
```
If `FinalizeAsync` throws (version mismatch), the exception is caught, logged, and the endpoint returns `success: true`. The bracket does NOT advance. The client UI shows "Result Verified!" toast. The bracket is silently stuck. There is no retry or notification to the organizer.

**Correct fix:** Either retry with a fresh version, or return a non-success status so the frontend can surface the failure.

### Broadcast on partial series (not just on completion)
```csharp
// 5. Broadcast bracket update (even for partial series progress)
await bracketHub.Clients.Group(...).SendAsync(BracketHubEvents.MatchUpdated, ...)
```
Score display on bracket UI updates even for BO3 in-progress games. ✅

### Map enrichment from veto
```csharp
var gameMapOrder = await vetoService.GetGameMapOrderAsync(id, ct);
var vetoGame = gameMapOrder.FirstOrDefault(g => g.GameNumber == gameNumber);
mapId ??= ...; mapName ??= vetoGame.MapName;
```
If report doesn't include map info, server enriches from veto data. ✅

---

## Part 26 — Report Dispute (`POST /api/matches/{id}/reports/{rid}/dispute`)

### Transaction
```csharp
using var tx = conn.BeginTransaction();
// 1. Mark report 'disputed'
// 2. Insert match_disputes
// 3. Insert tournament_disputes
// 4-5. Notify reporter + organizer
tx.Commit();
```
All dispute operations are in a single transaction. ✅

### Authorization
```csharp
var userCtx = ctx.Items["UserContext"] as UserContext;
if (userCtx is null) return Results.Unauthorized();
// ... no captain check, just membership via req.TeamId
```
### 🟡 MS4 (Medium): Dispute endpoint only checks authenticated, not captain or match membership

```csharp
}).RequireAuthorization("Authenticated");
```
No check that the caller is a captain, or that their team is in the match. Any authenticated user who knows the matchId and reportId can file a dispute, attributing it to any teamId they claim.

Compare to `/reports` which verifies captain, and `/accept` which verifies captain+match-member.

### Duplicate dispute creation
No `ON CONFLICT` guard. Multiple disputes can be created for the same match_id. The `GET /api/matches/{id}/dispute` returns `LIMIT 1 ORDER BY created_at DESC`, so only the latest is shown. Old ones silently accumulate. No impact on correctness but clutters the `match_disputes` table.

---

## Part 27 — Check-in (`POST /api/matches/{id}/checkin`)

### Authorization
```csharp
var isMember = await conn.QuerySingleOrDefaultAsync<bool>(
    "SELECT EXISTS(... WHERE team_id = @teamId AND user_id = @userId AND is_active = TRUE)");
if (!isMember) return Results.Forbid();
```
Verifies caller is active member of the claiming team. ✅

### 🟡 MS5 (Medium): No check that team is in the match

Same class as MS1. Verifies user is member of `req.TeamId`, but does not verify that team is `team1_id` or `team2_id` on this specific match. Any team member can check in any match on behalf of any team.

```sql
ON CONFLICT (match_id, team_id) DO NOTHING
```
Idempotent. ✅

---

## Part 28 — Time Proposals

### Proposal submission (POST `/time-proposals`)
```csharp
var captainTeam = await conn.QuerySingleOrDefaultAsync<string?>(
    "SELECT tm.team_id FROM team_members tm
     JOIN brkt_matches bm ON (bm.team1_id = tm.team_id OR bm.team2_id = tm.team_id)
     WHERE bm.id = @matchId AND tm.user_id = @userId AND tm.role = 'captain'");
if (captainTeam is null) return Results.Forbid();
```
Captain of a team in this match only. ✅

### Accept proposal (POST `/time-proposals/{id}/accept`)
Same captain+match validation. ✅  
Atomic CTE: marks proposal accepted + updates `brkt_matches.scheduled_time` in one statement. ✅

### Counter proposal (POST `/time-proposals/{id}/counter`)
Same captain+match validation. ✅  
Atomic CTE: marks old as `countered` + inserts new proposal. ✅

### 🟢 MQ1 (Quality): A captain can accept their own time proposal

Same pattern as MS2 — no check that acceptor is the opposing team's captain. Any captain in the match (including the proposer) can accept. Frontend prevents it via UI, but no server-side guard.

---

## Part 29 — Dispute Resolution (`PUT /api/matches/{matchId}/disputes/{disputeId}/resolve`)

### Authorization — CORRECT this time
```csharp
var isOrganizer = await conn.QuerySingleOrDefaultAsync<bool>(
    "SELECT EXISTS(
        SELECT 1 FROM match_disputes md
        JOIN brkt_matches bm ON bm.id = md.match_id
        JOIN brkt_versions v ON v.id = bm.version_id
        JOIN tournaments t ON t.id = v.tournament_id
        WHERE md.id = @disputeId AND t.organizer_id = @userId
    )");
if (!isOrganizer && !userCtx.Roles.Contains("admin")) return Results.Forbid();
```
Full per-tournament organizer check + admin bypass. ✅ This is the pattern BB11 should follow.

### Notification to both parties
Notifies disputing user AND original reporter. ✅

---

## Part 30 — Match Verification (`GET /api/matches/{id}/verify`)

```csharp
var isOrgOrStaff = await conn.QuerySingleOrDefaultAsync<bool>(
    "SELECT EXISTS(SELECT 1 FROM tournaments t WHERE t.id = @tid AND (t.organizer_id = @uid OR EXISTS (
        SELECT 1 FROM tournament_staff ts WHERE ts.tournament_id = @tid AND ts.user_id = @uid AND ts.status = 'active'
    )))");
if (!isOrgOrStaff) return Results.Forbid();
```
Organizer OR active staff member. ✅ Staff access pattern is correct here.

Returns reports + riot accounts + games. No sensitive data leakage. ✅

---

## Part 31 — Scheduling Endpoints

### Per-tournament organizer check
All organizer scheduling endpoints (scheduling-config, schedule-bulk, scheduled-time) verify:
```csharp
SELECT EXISTS(... JOIN tournaments t WHERE ts.id = @stageId AND t.organizer_id = @userId)
if (!isOrganizer && !userCtx.Roles.Contains("admin")) return Results.Forbid();
```
Full per-tournament check on all scheduling mutations. ✅

### Bulk schedule
```csharp
UPDATE brkt_matches m
SET scheduled_time = u.scheduled_time
FROM UNNEST(@ids::uuid[], @times::timestamptz[]) AS u(id, scheduled_time)
WHERE m.id = u.id
```
Single UNNEST query for all updates. Efficient. ✅

### 🟡 MS6 (Medium): schedule-bulk does not verify match IDs belong to the stage

The UNNEST update sets `scheduled_time` on `brkt_matches` where `m.id = u.id` — no join back to `brkt_versions.stage_id`. An organizer who passes match IDs from a different tournament could update those matches' scheduled times (assuming they know the UUIDs). Low practical risk (UUID guessing) but a logical isolation gap.

---

## MatchSystemEndpoints Summary

### 🔴 Critical
| # | Description | Impact |
|---|-------------|--------|
| MS3 | Version conflict in auto-finalize silently swallowed — bracket doesn't advance, client sees "success" | Bracket silently stuck after concurrent acceptance |

### 🟡 Medium
| # | Description | Impact |
|---|-------------|--------|
| MS1 | Report submission doesn't verify reporting team is in the match | Ghost reports from unrelated teams in DB |
| MS2 | Accept endpoint doesn't prevent a team from accepting their own report | Self-verification (frontend guards but no server check) |
| MS4 | Dispute endpoint has no captain or match-membership check | Any authenticated user can file a dispute on any match |
| MS5 | Check-in doesn't verify team is in the match | Any team member can register check-in for any match |
| MS6 | Bulk schedule doesn't restrict match IDs to the requesting stage | Cross-stage scheduled_time overwrite possible |

### 🟢 Quality
| # | Description |
|---|-------------|
| MQ1 | Captain can accept their own time proposal (UI guards, no server check) |

---

## Revised Final Priority (All Files Combined)

### Immediate Security
1. **BB12** — `/api/matches/{matchId}/finalize`: change to `"Organizer"` + add per-match ownership check
2. **BB11** — Bracket PUT/DELETE/advance-byes/walkover: add per-tournament ownership checks (pattern from `PUT /api/matches/{matchId}/scheduled-time`)
3. **MS3** — Version conflict in accept-auto-finalize: retry or surface failure to client instead of silent swallow
4. **MS2** — Accept endpoint: add server-side check preventing same team from accepting own report

### Data Integrity
5. **BB6** — Wrap `SaveGraphAsync` in a transaction
6. **BB1** — Normalize `"live"` vs `"in_progress"` status across domain + DB
7. **MS1** — Verify reporting team is in the match on report submission

### Correctness
8. **BB4** — Tie score on `/finalize` auto-detect: define and document intended behavior
9. **BB8** — GraphValidator champion check: `!= 1` not `== 0`
10. **BB2** — Swiss maxRounds: use total participants not standings count
11. **B6** — Frontend version picker: prioritize `active` status

### Visual / UX
12. **B1** — LB slot-based centering in BracketRenderer
13. **B2** — DE Grand Final X position fix

---

# PART II — Backend (.NET) Bracket Audit

**Date:** 2026-03-20  
**Scope:** `D:\esportra-backend\src\Esportra.Core\Bracket\` and `D:\esportra-backend\src\Esportra.Api\Endpoints\`  
**Files Audited:**

| File | Purpose |
|------|---------|
| `BracketModels.cs` | Domain records: BracketVersion, BracketNode, BracketEdge, BracketGraph, TeamStanding |
| `IBracketGenerator.cs` | Interface for all generators |
| `SingleEliminationGenerator.cs` | Backend SE generator |
| `DoubleEliminationGenerator.cs` | Backend DE generator |
| `SwissGenerator.cs` | Swiss R1 generator + SwissNextRoundService |
| `RoundRobinGenerator.cs` | Circle-algorithm RR generator with scheduling |
| `MatchFinalizationService.cs` | Score save, winner determination, bracket advancement, notifications |
| `BracketPersistenceService.cs` | SaveGraph, AutoAdvanceByes, Reset, Clear |
| `GraphValidator.cs` | Cycle detection, slot collision, champion node |
| `StandingsService.cs` | Points/Buchholz/ScoreDiff standings calculation |
| `BracketEndpoints.cs` | Generate, persist, advance, publish, cache, graph endpoints |
| `MatchEndpoints.cs` | Scan, process, finalize, walkover, reset match endpoints |

---

## Part 13 — BracketModels.cs

All domain records are C# 9+ sealed records. No mutable state. ✅

```csharp
public sealed record BracketNode(
    Guid    Id, Guid VersionId, int RoundIndex, int MatchNumber,
    string  BracketType,   // winners | losers | final | group | swiss_round
    string  Status,        // pending | live | completed
    int     BestOf = 1,    Guid? Team1Id = null, ...
    double? X = null,      double? Y = null);
```

**BracketType values documented:**  
`winners`, `losers`, `final`, `group`, `swiss_round` — matches DB values. ✅

**Status note:** `BracketPersistenceService.cs:55` maps `"live"` → `"in_progress"` on INSERT. But `BracketModels.cs` says Status = `"live"`. There is a **status naming mismatch** between the domain model and the DB column: the domain uses `"live"`, the DB stores `"in_progress"`. If the DB ever returns `"in_progress"` to the frontend, and the frontend checks for `"live"`, the check will fail.

### 🔴 BB1 (Critical): Domain Status = "live" vs DB status = "in_progress"

`BracketPersistenceService.cs` line 55:
```csharp
status = node.Status == "live" ? "in_progress" : node.Status,
```
DB stores `"in_progress"`. `BracketModels.cs` documents `"live"`. Any code reading status from DB (e.g. advancement checks, frontend type `BracketNode.Status`) will get `"in_progress"` but may compare against `"live"`. **Frontend `types/bracket-graph.ts` must check for `"in_progress"` not `"live"` when rendering live/active matches.** If frontend currently checks `status === "live"`, match-in-progress highlighting will silently not work.

---

## Part 14 — SingleEliminationGenerator.cs

### Round count formula
```csharp
int P          = (int)Math.Pow(2, Math.Ceiling(Math.Log2(targetSize)));
int fullRounds = (int)Math.Round(Math.Log2(P));
int numRounds  = Math.Max(1, fullRounds - (int)Math.Round(Math.Log2(targetRemaining)));
```

**Matches frontend exactly.** Uses `Math.Round(Math.Log2(P))` for fullRounds — this is safe for powers of 2 since Log2 is exact. ✅

### Seeding — GetStandardBracketSlots(n)
```csharp
slots[i]         = upper[i] * 2;       // top half seeds
slots[n - 1 - i] = upper[i] * 2 + 1;  // bottom half seeds (reversed)
```

Matches frontend exactly. 8-team trace:
- n=2 → [0,1]
- n=4 → [0,2,3,1]  
- n=8 → [0,4,6,2,3,7,5,1]

Pairing: Seed1v8, Seed4v5, Seed2v7, Seed3v6 ✅

### Edge creation
```csharp
var nextMatchNum = (int)Math.Ceiling((i + 1) / 2.0);
edges.Add(... TargetSlot: (i + 1) % 2 == 1 ? 1 : 2);
```
Match 1 and 2 → next match 1 (slot 1, slot 2). Match 3 and 4 → next match 2 (slot 1, slot 2). ✅

**Backend SE generator is correct. Matches frontend implementation 1:1.**

---

## Part 15 — DoubleEliminationGenerator.cs

### LB round count formula
```csharp
int numUpperRounds = (int)Math.Round(Math.Log2(P));
int numLowerRounds = 2 * numUpperRounds - 2;
```
For P=8: numUpperRounds=3, numLowerRounds=4 ✅ (standard: 4 LB rounds for 8-team DE)  
For P=16: numUpperRounds=4, numLowerRounds=6 ✅  
For P=4: numUpperRounds=2, numLowerRounds=2 ✅

### LB match count per round
```csharp
int matchesInRound = (int)Math.Pow(2, Math.Floor((numLowerRounds - 1 - r) / 2.0));
```
For P=8 (numLowerRounds=4):
- r=0: 2^Floor(3/2) = 2^1 = 2 ✅ (2 drop-in matches)
- r=1: 2^Floor(2/2) = 2^1 = 2 ✅ (2 LB-vs-LB matches)
- r=2: 2^Floor(1/2) = 2^0 = 1 ✅ (1 LB semi)
- r=3: 2^Floor(0/2) = 2^0 = 1 ✅ (1 LB final)

**Correct for 8-team.** Matches frontend formula. ✅

### WB → LB loser drop formula
```csharp
int lr = r == 0 ? 0 : 2 * r - 1;
int dropMatchNum = r == 0 ? (int)Math.Ceiling((i + 1) / 2.0) : (i + 1);
```
For P=8:
- WB R0 (4 matches) → LB R0, matches 1-2 (pairs: M1,M2→LB1; M3,M4→LB2) ✅
- WB R1 (2 matches) → LB R1 (`lr=1`), match 1 and 2 ✅
- WB R2 (1 match)  → LB R3 (`lr=3`), match 1 ✅

LB R2 is WB-loser-free (pure LB-vs-LB round). **This is standard DE.** ✅

### WB upper final → LB loser path
```csharp
if (matchMap.TryGetValue($"losers-{numLowerRounds - 1}-1", out var lbFinal))
    edges.Add(... upperFinal.Id, lbFinal.Id, "loser", 1);
```
WB Final loser drops to LB Final slot 1. ✅

### LB → GF edge
```csharp
if (matchMap.TryGetValue($"losers-{numLowerRounds - 1}-1", out var lowerFinal))
    edges.Add(... lowerFinal.Id, gf.Id, "winner", 2);
```
LB Final winner → GF slot 2. WB Final winner → GF slot 1. ✅

### LB internal advancement edges
```csharp
int nextMatchNum = r % 2 == 0 ? (i + 1) : (int)Math.Ceiling((i + 1) / 2.0);
int slot = r % 2 == 0 ? 2 : ((i + 1) % 2 == 1 ? 1 : 2);
```
- Even LB rounds (drop-in rounds): same match index, slot 2 (WB loser fills slot 1, LB winner slot 2)  
- Odd LB rounds (consolidation rounds): ceiling merge, standard 1/2 alternating slot

**Verified for 8-team LB R0→R1→R2→R3 chain.** ✅

### Grand Final node
```csharp
var gf = new BracketNode(Id:..., RoundIndex: numUpperRounds, MatchNumber: 1, BracketType: "final", ...);
matchMap["final-0-1"] = gf;
```
Note: GF is keyed as `"final-0-1"` but its `RoundIndex` is `numUpperRounds`. This is fine because the GF key is only used for internal edge wiring, not for round-based lookups. ✅

**Backend DE generator is correct. Matches frontend implementation.** ✅

---

## Part 16 — SwissGenerator.cs

### Round 1 pairing (in-memory)
```csharp
// Slide pairing: top half vs bottom half
int half       = (groupTeams.Count + 1) / 2;
var topHalf    = groupTeams.Take(half).ToList();
var bottomHalf = groupTeams.Skip(half).ToList();
```
Standard Swiss R1 slide pairing. For 8 teams: top half = [1,2,3,4], bottom half = [5,6,7,8]. Matches: 1v5, 2v6, 3v7, 4v8. ✅

BYE handling: if `i >= bottomHalf.Count`, `t2 = null`. BYE match is stored with `Team2Id = null`. ✅

### Max rounds validation
```csharp
int maxRounds = (int)Math.Ceiling(Math.Log2(standingsList.Count));
if (currentRound >= maxRounds)
    return (false, $"Round limit reached ({maxRounds} rounds).");
```
For 8 teams: maxRounds = 3 (log2(8) = 3). Standard Swiss upper bound. ✅

### Subsequent round pairing (SwissNextRoundService)
```csharp
// Group by score, sort descending, pair within score group
// Floaters carry over to next score group
// Rematch avoidance via playedMap HashSet
```
Implementation:
1. Fetches full match history for rematch tracking ✅
2. Groups teams by Points (descending) ✅
3. Pairs within each score group, closest rank first (top of group is taken first) ✅
4. Floaters (odd-out from score group) merge with next lower score group ✅
5. If no valid opponent exists (all rematches), team becomes a floater ✅
6. Force-pairs remaining floaters at end (allows rematch as last resort) ✅
7. Unpaired single floater gets a BYE (pairings.Add((floaters[0], null))) ✅

### 🟡 BB2 (Medium): Swiss max rounds uses team count from standingsList, not total teams

```csharp
var standingsList = await standings.CalculateStandingsAsync(stageId, ct: ct);
int maxRounds = (int)Math.Ceiling(Math.Log2(standingsList.Count));
```
`standingsList` only includes teams that have played at least one completed match. If a team has 0 completed matches (e.g. all their R1 matches are still pending), they won't appear in standingsList. This can cause `maxRounds` to be calculated from a smaller count than the actual participant pool, potentially cutting the tournament short by 1 round.

**Correct fix:** Use total registered teams count for `maxRounds`, not standings count.

### 🟡 BB3 (Medium): Swiss group assignment in next-round ignores teams with no completed games

`groupMap` is built from match history. A team that was paired in R1 but whose match is still `pending` has `team1_id`/`team2_id` in `brkt_matches` but `status != 'completed'` — so they appear in `groupMap` (match history) but NOT in `standingsList` (only completed). This split means they could be assigned to a group but not included in pairings.

### 🟢 BQ1 (Quality): matchCounter starts from history.Count, not max(match_number)

```csharp
int matchCounter = existingCount + 1;
```
`existingCount = history.Count` = count of all matches (including BYE matches). `match_number` in the DB is assigned sequentially from the generator. Since R1 BYEs are excluded from history by... actually they ARE included since history fetches all matches without status filter. So matchCounter = all prior matches + 1 = correct sequential numbering. ✅ (not a bug, just unusual)

---

## Part 17 — RoundRobinGenerator.cs

### Snake seeding
```csharp
int groupIndex = cycle % 2 == 0 ? idx % numGroups : numGroups - 1 - (idx % numGroups);
```
For 2 groups, 8 teams:  
- Cycle 0 (idx 0,1): group 0, 1 (normal)  
- Cycle 1 (idx 2,3): group 1, 0 (reversed)  
- Cycle 2 (idx 4,5): group 0, 1 (normal)  
- Cycle 3 (idx 6,7): group 1, 0 (reversed)

Distribution: Group A = {0,3,4,7}, Group B = {1,2,5,6} (by seed). Standard snake seeding for balanced groups. ✅

### Circle algorithm
```csharp
// Fix position 0, rotate the rest
var newCircle = new List<(Guid, string)>(n) { circle[0] };
newCircle.Add(circle[n - 1]);
for (int i = 1; i < n - 1; i++)
    newCircle.Add(circle[i]);
circle = newCircle;
```
Standard Berger/circle rotation: fix element at index 0, rotate remainder one step right. ✅

BYE addition for odd teams: `participants.Add((Guid.Empty, "BYE"))`. BYE matches excluded: `if (t1.Item1 == Guid.Empty || t2.Item1 == Guid.Empty) continue`. ✅

Total matches = n*(n-1)/2 for n teams (or (n-1)*n/2 = same thing). ✅

### Scheduling
```csharp
var roundDate = startDate.Value.AddDays(ri);  // ri = round index
```
Each round gets one day. For a group with 7 rounds (8 teams), rounds span 7 consecutive days. Reasonable default. ✅

**Backend RR generator is correct.** ✅

---

## Part 18 — MatchFinalizationService.cs

This is the most important service — it runs on every match completion.

### Transaction safety
```csharp
using var conn = db.CreateConnection();
using var tx = conn.BeginTransaction();
// ... all operations inside tx
tx.Commit();
// catch → tx.Rollback(); throw;
```
Full ACID transaction. All 5 steps (lock → version check → update → advance → log event) are atomic. ✅

### Pessimistic locking
```csharp
SELECT m.version, v.tournament_id
FROM public.brkt_matches m
JOIN public.brkt_versions v ON m.version_id = v.id
WHERE m.id = @matchId
FOR UPDATE
```
Row-level lock prevents concurrent finalization of the same match. ✅

### Optimistic concurrency
```csharp
if (actualVersion != expectedVersion)
    throw new InvalidOperationException($"Match version mismatch...");
```
Protects against stale client state. If two organizers try to finalize simultaneously, second one gets a 409 Conflict. ✅

### Winner/loser determination
The service takes `winnerId` and `loserId` as explicit inputs — it does not compute them itself. They are computed in the calling endpoint:
```csharp
// In MatchEndpoints.cs, /api/matches/{matchId}/process:
var winnerId = (Guid)report.winner_team_id;
var loserId  = winnerId == (Guid)match.team1_id ? (Guid)match.team2_id : (Guid)match.team1_id;
```
This is correct — loser is whichever team is NOT the winner. ✅

### 🔴 BB4 (Critical): /api/matches/{matchId}/finalize has tie-score bug

```csharp
// In /api/matches/{matchId}/finalize (auto-detect from scores):
winnerId = (int)m.team1_score >= (int)m.team2_score ? (Guid)m.team1_id : (Guid)m.team1_id;
```
Wait — reading actual code again:
```csharp
winnerId = (int)m.team1_score >= (int)m.team2_score ? (Guid)m.team1_id : (Guid)m.team2_id;
loserId  = winnerId == (Guid)m.team1_id ? (Guid)m.team2_id : (Guid)m.team1_id;
```
**On TIE (equal scores): team1 is declared winner.** This is deterministic but may not be desired. On a 0-0 tie (match abandoned, scores never set), both scores = 0, team1 always wins. This is a silent incorrect result. Same issue as frontend B8. **Tie score handling is undefined at both layers.**

### Bracket advancement
```csharp
await AdvanceTeamInternalAsync(conn, tx, matchId, winnerId, "winner");
await AdvanceTeamInternalAsync(conn, tx, matchId, loserId, "loser");
```
Reads `brkt_advancements` table for edges from this match, writes `team1_id` or `team2_id` in the target match. ✅

### 🟡 BB5 (Medium): NotifyIfMatchReady uses parameterized IN but Dapper doesn't expand @t1, @t2 for IN lists

```csharp
WHERE tm.team_id IN (@t1, @t2) AND tm.role = 'captain'
new { t1 = (Guid)match.team1_id, t2 = (Guid)match.team2_id }
```
Dapper handles two scalar parameters in an `IN ()` with literal `@t1, @t2` positionally — this actually works for exactly 2 params since they're expanded as separate parameters in the SQL, not as an array. However, it relies on SQL driver behavior. The correct Dapper idiom for `IN` is to pass a collection (which Dapper auto-expands). For 2 items, this is functionally correct but fragile if extended to 3+ teams. Not a current bug.

### Audit trail
```csharp
INSERT INTO public.match_completed_events (match_id, winner_id, loser_id, status)
VALUES (@matchId, @winnerId, @loserId, 'processed')
```
Every finalization is logged. ✅

---

## Part 19 — BracketPersistenceService.cs

### SaveGraphAsync
1. Queries max version_number for the tournament, increments by 1 ✅
2. Inserts `brkt_versions` record ✅
3. Inserts each node into `brkt_matches` (one by one — N round trips for N matches)
4. If node has X or Y coordinates, inserts into `brkt_layout` ✅
5. Inserts each edge into `brkt_advancements` ✅

### 🟡 BB6 (Medium): SaveGraphAsync inserts matches one-by-one (N+1 writes)

For a 128-team SE (127 matches + 254 edges = 381 rows), this is 381 sequential `INSERT` statements in a loop. No transaction wrapping the entire save. If the server crashes after saving 50 nodes but before saving edges, the DB is in an inconsistent state (matches with no advancement edges).

**No transaction on SaveGraphAsync.** ✅ Risk: partial save on crash.

**Performance:** For a 16-team bracket (15 matches, 14 edges, ~14 layout rows = ~43 inserts), acceptable. For 128-team (~381 inserts), slow.

**Correct fix:** Wrap `SaveGraphAsync` in a transaction and use UNNEST batch inserts.

### AutoAdvanceByesAsync

```csharp
bool hasT1 = match.team1_id is not null;
bool hasT2 = match.team2_id is not null;
if (!(hasT1 ^ hasT2)) continue; // both or neither → skip
```
Only processes matches with exactly one team. ✅

BYE score: `winScore = bestOf == 1 ? 13 : (int)Math.Ceiling(bestOf / 2.0)`. For BO1: 13, BO3: 2, BO5: 3. The `13` for BO1 seems to be Valorant-specific (13 rounds wins a Valorant game). This is a domain assumption baked in. ✅ (expected for Valorant platform)

```csharp
await conn.ExecuteAsync("UPDATE ... SET status = 'completed', winner_id = @winnerId, loser_id = null ...")
```
**loser_id = null for BYE matches.** This is correct — there's no loser in a BYE. ✅

### Reset
```csharp
if ((int)match.round_index == 0)
    // reset scores/status only, keep team1_id/team2_id (seeded teams)
else
    // clear team1_id, team2_id, scores, status (derived teams)
```
Correctly preserves R0 seeding while clearing advanced teams. ✅  
Also deletes `brkt_match_events`. ✅

### 🟡 BB7 (Medium): Reset doesn't restore BYE-advanced teams

After Reset, a round_index > 0 match that previously had team X advanced into it (via BYE in a prior run) will have `team1_id = null`. But the BYE match at round_index = 0 was reset to `status = 'pending'` with its teams intact. Running `AutoAdvanceByes` again should re-advance — BUT `AutoAdvanceByes` checks `status = 'pending'` AND `hasT1 ^ hasT2`. After Reset, the BYE match at R0 is pending with one team, so `AutoAdvanceByes` will re-advance. ✅ (self-correcting)

---

## Part 20 — GraphValidator.cs

### Cycle detection (DFS)
```csharp
bool Dfs(Guid nodeId)
{
    if (recStack.Contains(nodeId)) return true;  // back edge = cycle
    if (visited.Contains(nodeId))  return false; // already fully explored
    visited.Add(nodeId); recStack.Add(nodeId);
    // ... recurse on children ...
    recStack.Remove(nodeId);
    return false;
}
return nodes.Any(n => Dfs(n.Id));
```
Standard iterative DFS with recursion stack tracking. Correct cycle detection. ✅  
Stack depth = max path length in bracket = log2(N) for SE. No stack overflow for any realistic bracket size. ✅

### Slot collision check
```csharp
var key = $"{e.TargetMatchId}-{e.TargetSlot}";
incoming[key] = incoming.GetValueOrDefault(key) + 1;
// then: if count > 1 → error
```
Correctly detects two edges targeting the same match+slot. ✅

### Champion check
```csharp
var potentialChampions = nodes.Where(n => !nodesWithOutgoingWinner.Contains(n.Id)).ToList();
if (potentialChampions.Count == 0)
    errors.Add("No champion node found (infinite loop?).");
```
Finds all nodes with no outgoing winner edge (sink nodes). Only reports error if there are ZERO such nodes (cycle). Does NOT validate that there is exactly ONE champion — a disconnected graph would have multiple sinks and this validator would not catch it.

### 🟡 BB8 (Medium): GraphValidator does not validate single champion

If a bracket generator bug creates two disconnected subgraphs (each with its own GF), `potentialChampions.Count` = 2 and the validator passes. The check should be `potentialChampions.Count != 1` for elimination brackets.

Currently: passes as long as count >= 1 (the `== 0` check only catches the fully-cyclic case).

---

## Part 21 — StandingsService.cs

### Points formula
- Win = 3 points ✅
- Loss = 0 points ✅
- Tie (no winner_id) = 1 point each ✅

### BYE handling
```csharp
else if (m.team1_id is Guid onlyT1)
{
    t1.Played++; t1.Wins++; t1.Points += 3; t1.ScoreDiff += t1Score;
}
```
Single-team match → counted as win for the present team. ✅

### Buchholz calculation
```csharp
// Second pass:
t1.Buchholz += t2.Points;
t2.Buchholz += t1.Points;
```
Standard Buchholz (sum of opponents' final points). ✅  
Note: this is a simple/static Buchholz, not median Buchholz (which drops highest and lowest opponent score). Acceptable for Swiss.

### Sort order
```
Points → Buchholz → ScoreDiff → Wins
```
Standard tiebreaker cascade. ✅

### 🟡 BB9 (Medium): Buchholz computed from final points, not points-at-time-of-match

Standard Buchholz should use each opponent's points at the time of the match, or final standing points — both interpretations exist in Swiss. This implementation uses final standing points. This means early-round results affect Buchholz by the same magnitude as late-round results. This is technically a simplified Buchholz but widely accepted in online esports. Not a correctness bug, a design choice.

### 🟡 BB10 (Medium): StandingsService.CalculateStandingsAsync fetches by stage_id but no index hint

Query joins `brkt_matches → brkt_versions` on `stage_id`. If `brkt_versions.stage_id` is not indexed, this causes a full table scan on large datasets. Not a correctness issue, a performance issue.

---

## Part 22 — BracketEndpoints.cs

### POST /api/brackets/generate

```csharp
IBracketGenerator generator = req.Format.ToLowerInvariant() switch
{
    "double_elimination" => new DoubleEliminationGenerator(),
    "round_robin"        => new RoundRobinGenerator(),
    "swiss"              => new SwissGenerator(),
    _                    => new SingleEliminationGenerator(),  // default
};
```
Format routing is correct. ✅  
**Unknown format silently defaults to SE** — no warning returned to caller. A typo like `"single_elimination"` or `"se"` generates SE silently. Low risk since organizer UI controls the value, but worth noting.

```csharp
var errors = GraphValidator.Validate(graph);
if (errors.Count > 0) return Results.BadRequest(new { errors });
```
Validation gate before persistence. ✅

SignalR broadcast after generation. ✅

### POST /api/brackets/persist (frontend-generated bracket)

Authorization:
```csharp
var isOrganizer = await conn.QuerySingleOrDefaultAsync<bool>(
    "SELECT EXISTS(SELECT 1 FROM tournaments t JOIN tournament_stages ts ..."
    WHERE ts.id = @stageId AND (t.organizer_id = @userId OR t.organization_id IN (...))
```
Checks user is organizer of the tournament that owns the stage. ✅

Validates graph before save. ✅

### PUT /api/brackets/{versionId} (publish / status change)

On status = "active":
```csharp
var readyMatches = await conn.QueryAsync<dynamic>(
    "SELECT id, team1_id, team2_id FROM brkt_matches WHERE version_id = @versionId AND team1_id IS NOT NULL AND team2_id IS NOT NULL");
foreach (var m in readyMatches)
    await BracketPersistenceService.NotifyMatchReadyCaptainsAsync(conn, m.id, m.team1_id, m.team2_id);
```
Notifies captains of all immediately-ready matches on publish. ✅

### 🟡 BB11 (Medium): PUT /api/brackets/{versionId} does not check calling user is organizer of this version's tournament

```csharp
app.MapPut("/api/brackets/{versionId}", async (
    Guid versionId, HttpContext ctx, IDbConnectionFactory db, ...) => {
    var userCtx = ctx.Items["UserContext"] as UserContext;
    if (userCtx is null) return Results.Unauthorized();
    // ... immediately updates status without organizer check ...
    await conn.ExecuteAsync("UPDATE brkt_versions SET status = @status WHERE id = @versionId", ...)
```
The endpoint verifies `RequireAuthorization("Organizer")` (role-level), but does NOT verify the calling user is the organizer of *this specific tournament*. Any tournament organizer on the platform can publish/archive any bracket version if they know the versionId UUID. While UUID guessing is impractical, this is a data isolation violation.

**Compare to POST /api/brackets/persist** which does check `t.organizer_id = @userId`. The PUT endpoint is inconsistent.

### GET /api/brackets/{versionId}

Returns `cached_ui_state` from `brkt_versions`. No authentication required. Public access. ✅

### GET /api/brackets/{versionId}/graph

Returns full graph (all nodes, edges, team names). No authentication required. Public access. ✅

### POST /api/brackets/{versionId}/advance-byes

Calls `AutoAdvanceByesAsync`. RequireAuthorization("Organizer"). No per-tournament organizer check (same BB11 issue but lower severity for advance-byes).

### POST /api/swiss/next-round

```csharp
app.MapPost("/api/swiss/next-round", ...).RequireAuthorization("Organizer");
```
No per-tournament organizer check. Any organizer can trigger next round for any Swiss bracket. Same class of issue as BB11.

---

## Part 23 — MatchEndpoints.cs (Relevant to Bracket)

### POST /api/matches/{matchId}/process

Full flow:
1. Fetches accepted report ✅
2. Validates report.status == "accepted" ✅
3. Gets current match version for optimistic lock ✅
4. Computes winnerId from report.winner_team_id, loserId from the other team ✅
5. Calls `FinalizeAsync(matchId, version, winnerId, loserId, scores)` ✅
6. Marks report as "processed" ✅
7. Broadcasts via MatchHub ✅

**Correct flow for the normal path.** ✅

### POST /api/matches/{matchId}/finalize (force finalize)

```csharp
// Auto-detect winner/loser from scores if not provided:
winnerId = (int)m.team1_score >= (int)m.team2_score ? (Guid)m.team1_id : (Guid)m.team2_id;
```
TIE BUG already noted as BB4. Also: this endpoint has `.RequireAuthorization("Authenticated")` not `"Organizer"` — **any authenticated user can force-finalize any match** if they know the matchId.

### 🔴 BB12 (Critical): /api/matches/{matchId}/finalize has no authorization for who can call it

```csharp
}).RequireAuthorization("Authenticated");
```
Should be `"Organizer"` AND should check that the organizer owns the tournament containing this match. Any player with a valid JWT token can hit this endpoint and finalize any match.

### POST /api/matches/{matchId}/award-walkover

```csharp
}).RequireAuthorization("Organizer");
```
Role check present. ✅ But no per-tournament check (same BB11 class). Medium severity.

### POST /api/matches/{matchId}/reset

This is a deeply destructive endpoint:
- Deletes game results ✅
- Undoes advancements (nulls out team slots in future matches) ✅
- Deletes veto state ✅
- Soft-closes disputes ✅
- Deletes all reports ✅
- Deletes time proposals + check-ins ✅

```csharp
}).RequireAuthorization("Organizer");
```
Role check but no per-tournament check. Any organizer can reset any match. Same BB11 class.

### 🟡 BB13 (Medium): match/reset scores set to 0, not NULL on target matches

```csharp
team1_score = 0, team2_score = 0
```
In `AdvanceTeamInternalAsync` / reset SQL (line ~515):
```sql
team1_score = 0, team2_score = 0
```
After reset, target matches show `0` scores rather than `NULL`. Frontend renders `0` rather than `-`. This is the same as frontend B9. Both layers have this issue.

---

## Backend Summary Table

### 🔴 Critical Bugs

| # | File | Description | Impact |
|---|------|-------------|--------|
| BB1 | `BracketModels.cs` / `BracketPersistenceService.cs` | Domain model uses `"live"` for in-progress status but DB stores `"in_progress"`. Frontend check for `"live"` status will silently fail. | Match-in-progress highlighting broken |
| BB4 | `MatchEndpoints.cs` (~line 395) | Tie score (equal scores) always awards team1 as winner in `/finalize` auto-detect path | Silent incorrect result on tied/abandoned matches |
| BB12 | `MatchEndpoints.cs` | `/api/matches/{matchId}/finalize` uses `"Authenticated"` not `"Organizer"` — any player can force-finalize any match | Security: unauthorized match manipulation |

### 🟡 Medium Bugs

| # | File | Description | Impact |
|---|------|-------------|--------|
| BB2 | `SwissGenerator.cs:87` | `maxRounds` computed from standings count (completed-match teams only), not total participants | Swiss may end 1 round early if any R1 matches still pending |
| BB3 | `SwissGenerator.cs:104-118` | Teams in `groupMap` (from match history) but not in `standingsList` (completed-only) can cause pairing gaps | Missing pairings for teams with all-pending matches |
| BB5 | `MatchFinalizationService.cs:163` | `IN (@t1, @t2)` — non-idiomatic Dapper for IN clause; fragile for future extension to 3+ teams | Fragile pattern |
| BB6 | `BracketPersistenceService.cs:39-73` | SaveGraphAsync has no transaction — partial bracket save possible on crash | Data integrity on server crash |
| BB7 | `BracketPersistenceService.cs:148-161` | Reset doesn't re-advance BYEs automatically; requires separate advance-byes call | Operator step required after reset |
| BB8 | `GraphValidator.cs:35-38` | Champion check passes if count >= 1, not count == 1; disconnected bracket graph passes validation | Silent structural bracket bug passes validation |
| BB9 | `StandingsService.cs:82-84` | Buchholz uses final points, not time-of-match points | Simplified Buchholz, acceptable for most use cases |
| BB10 | `StandingsService.cs:14-25` | No index hint for stage_id join; potential scan on large datasets | Performance |
| BB11 | `BracketEndpoints.cs` | PUT/DELETE bracket endpoints check role but not per-tournament ownership | Any organizer can modify any bracket |
| BB13 | `MatchEndpoints.cs:515` | Reset sets scores to 0 not NULL on target matches | Display shows "0" not "-" after reset |

### 🟢 Code Quality

| # | File | Description |
|---|------|-------------|
| BQ1 | `SwissGenerator.cs:122` | matchCounter = existingCount+1; includes BYE matches in count, giving non-sequential match_numbers in some edge cases |
| BQ2 | `BracketEndpoints.cs:41` | Unknown bracket format silently defaults to SE with no warning to caller |
| BQ3 | `BracketPersistenceService.cs` | No transaction on SaveGraphAsync; should wrap all inserts in one transaction |

---

## Cross-Layer Comparison (Frontend vs Backend)

| Aspect | Frontend | Backend | Match? |
|--------|---------|---------|--------|
| SE seeding algorithm | Recursive GetStandardBracketSlots | Same recursive algorithm | ✅ Identical |
| SE round count | `Math.log2(P) - Math.log2(targetRemaining)` | Same formula | ✅ Identical |
| SE edge formula | `Math.ceil((i+1)/2)` | Same | ✅ Identical |
| DE LB round count | `2 * upperRounds - 2` | Same | ✅ Identical |
| DE LB match count per round | `Math.pow(2, Math.floor((LBR-1-r)/2))` | Same | ✅ Identical |
| DE WB→LB loser drop | `r===0 ? 0 : 2*r-1` | Same | ✅ Identical |
| Swiss R1 slide pairing | top-half vs bottom-half | Same | ✅ Identical |
| Swiss subsequent rounds | Delegates to API (`/api/swiss/next-round`) | `SwissNextRoundService` | ✅ Correct delegation |
| RR circle algorithm | Standard rotation | Same rotation (fix pos-0, rotate rest) | ✅ Identical |
| Status "live" vs "in_progress" | Frontend uses "live" in type | DB stores "in_progress" | ❌ Mismatch (BB1) |
| Tie score handling | Team2 wins on tie | Team1 wins on tie | ❌ Inconsistent (both wrong) |
| BYE score | Not stored (no score on BYE in frontend gen) | 13 (BO1), ceil(bestOf/2) (BOx) | Diverges (backend-only concern) |

---

## Final Priority Fix Order (All Layers)

### Immediate (Security / Data Integrity)
1. **BB12** — Change `/api/matches/{matchId}/finalize` from `"Authenticated"` to `"Organizer"` + add per-match ownership check
2. **BB11** — Add per-tournament ownership check to PUT/DELETE bracket endpoints, award-walkover, match-reset
3. **BB6** — Wrap `SaveGraphAsync` in a transaction

### High (Correctness)
4. **BB1** — Normalize status: either change DB to store `"live"` or add mapping on read so frontend receives consistent values
5. **BB4 + B8** — Define tie-score behavior (who wins on equal scores) and implement consistently in both frontend optimisticBracket and backend `/finalize`
6. **BB8** — Fix `GraphValidator` champion check to `!= 1` not `== 0`
7. **B6** — Fix frontend version picker to prioritize `active` status

### Medium (Visual / Swiss correctness)
8. **BB2** — Fix Swiss maxRounds to use total registered participants count
9. **B1** — Fix LB slot-based centering in `BracketRenderer`
10. **B2** — Fix DE Grand Final X position

### Low (Quality)
11. BB5, BB13/B9 (null vs 0 scores), BQ2, Q1, Q6/Q7
