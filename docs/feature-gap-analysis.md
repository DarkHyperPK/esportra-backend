# Esportra Feature Gap Analysis

Comparison against FACEIT, Start.gg, Battlefy, Challengermode, Toornament, ESEA.

## High Impact — Expected on any serious tournament platform

### 1. Anti-Cheat Integration
No VAC/FACEIT AC/EAC status verification. Players can join tournaments without proving clean accounts.

**Practical approach:** Since we have Steam account linking, call Steam's `ISteamUser/GetPlayerBans` at tournament registration time and reject players with recent VAC bans. One-endpoint integration (~1 day). For FACEIT AC, need their API key to check AC-verified matches. Cannot enforce kernel-level AC from a web platform — we gatekeep entry based on third-party AC status.

### 2. Tournament Series / Seasons / Leagues
No recurring tournament structure. Can't link qualifiers to main event or run weekly leagues with cumulative points.

**Note:** Season feature was previously implemented and removed (migrations: `drop_season_feature`, `season_feature_final_cleanup`, `purge_season_audit_logs`). Simplest version: `series` table + `series_tournaments` join + `series_standings` view aggregating points. ~2-3 days.

### 3. Automated Seeding
**RESOLVED:** Seeding is random by design for fairness. Not a matchmaking platform — no ELO tracked.

### 4. Auto-DQ / No-Show Timers
**PARTIALLY IMPLEMENTED:**
- Match-level: `CheckinWalkoversJob` runs every 60s, auto-forfeits when check-in window closes
- Tournament-level: Schema fields exist (`check_in_required`, `check_in_deadline`, `auto_remove_unchecked`) but NO background job enforces them
- Manual endpoint exists: `POST /api/tournaments/{id}/remove-unchecked`

**Enhancement needed:** Background job to auto-DQ tournament participants who don't check in by deadline.

### 5. Free Agent / LFT Marketplace
No way for solo players to find teams or for teams to recruit.

**Anti-spam design:**
- LFT is a flag + preferences on existing profile (not separate accounts)
- Activity-gated visibility (active in last 14 days)
- Tournament participation requirement (1+ tournament OR linked game account)
- Auto-expire listings after 30 days, require manual renewal
- Rate-limit contact (max 5 outbound/day)
- Report + hide system

### 6. Custom Rulebook / Rules Engine
**RESOLVED:** Tournament rules section exists for organizers. Official platform-wide rulebook is a content/docs task, not code.

---

## Medium Impact — Differentiators on top platforms

### 7. Scrim / Practice Match System
**RESOLVED:** Only feasible for CS2 (we control servers via DatHost). Other games need publisher partnerships or BYOS.

### 8. Embeddable Widgets
**API IS READY** — brackets are publicly accessible without auth. Missing pieces:
- No dedicated `/embed` endpoint with minimal HTML/JS output
- No embed code generator for organizers (iframe snippet)
- CORS allows our domains but not arbitrary third-party domains
- Frontend embed view is the missing piece (~2 days)

### 9. Public API / Webhooks
No documented API for third-party integrations or webhook system for event-driven notifications.

### 10. Crowdfunded Prize Pools
Community prize pool contributions.

**How it works:** Tournament has base prize + public "contribute" page. Community donates ($1-$25). Platform takes cut (0-15%). Live progress bar on tournament page.

**Implementation:** `crowdfund_enabled` flag + `crowdfund_goal` on tournaments, `tournament_contributions` table, public contribute endpoint, display `base_prize + SUM(contributions)`. ~3-5 days (payment processing is the hard part).

### 11. Tournament Templates / Cloning
No quick-start from previous tournament config. Organizers running weekly events rebuild from scratch.

### 12. Player Stats Dashboard / Match History
**CS2 STATS WORKING:** MatchZy captures 30+ stat columns per player per map.

**MatchZy/CS2 Server Integration Status:**

Working:
- DatHost server provisioning (16 regions)
- Auto-provision on veto complete
- MatchZy config endpoint (teams, maps, sides, BO1/3/5)
- RCON-based MatchZy setup
- Live score updates (round_end -> SignalR broadcast)
- Per-map finalization + 30+ player stat columns
- Series completion -> bracket advancement
- Fire-and-forget server deletion

Incomplete:
- Demo file storage (handler receives filename, doesn't store) — 2-3 days
- Demo download/playback endpoints — 1-2 days after storage
- Server error recovery (no auto-restart) — 2 days
- Server health monitoring — 1-2 days
- Cost tracking/billing (stored but unused) — 1 day

**Missing:** Unified player performance history across tournaments (win rate, avg placement, K/D).

### 13. VOD / Replay Linking
**Key insight:** CS2 .dem files are NOT video files. They require the CS2 engine to render. Cannot play in browser natively.

**Options:**
| Approach | Effort |
|----------|--------|
| Link Twitch/YouTube VODs to matches (`vod_url` field) | 1 day |
| Demo download (store .dem in S3, provide download links) | 2-3 days |
| Server-side demo rendering to video | Weeks, infra-heavy — NOT recommended |
| Embed Twitch clips/timestamps | 1 day |
| Third-party demo viewer (Leetify, noesis.gg) | Depends on API |

**Recommendation:** Short-term: `vod_url` on matches + embedded player. Medium-term: S3 demo storage + download. Long-term: Partner with demo analysis service.

---

## Lower Impact — Nice-to-haves

### 14. Multi-Language / Localization
No i18n infrastructure. All strings hardcoded in English.

**Implementation without breaking things:**
- Backend: `Accept-Language` header parsing + `ILocalizer` service + resource files (en.json, ar.json)
- Frontend: `react-i18next` with namespace-per-page lazy loading
- Effort: Backend infra 2-3 days, string extraction 3-5 days, frontend 2-3 weeks
- Key decision: Arabic (RTL) is significantly harder (requires layout mirroring)

### 15. Achievements / Badges / Gamification
Loyalty tiers exist for venues. No player competitive achievements.

**Design:**
- `achievement_definitions` table (slug, name, criteria jsonb, rarity, category)
- `user_achievements` table (user_id, achievement_id, unlocked_at, metadata)
- `AchievementEvaluationService` runs after match/tournament completion
- Examples: First Tournament Win, 5-Win Streak, Ace, Clutch Master, 10 Tournaments Played
- Effort: 3-5 days infrastructure + first 10 achievements

### 16. Tournament Recommendations
No personalized suggestions based on skill, game, region, or participation history.

### 17. Qualifier -> Main Event Linking
**RESOLVED within single tournament:** Multi-stage support handles Swiss qualifier -> Single Elim finals within one tournament. Cross-tournament linking (separate events feeding winners) does NOT exist but the stage system covers most use cases.

### 18. Discord Bot Integration
**Current state:** Bot token configured, DMs working (8 types), auto-join guild, user opt-in/out.

**Missing:**
- Role management, channel management, slash commands, webhooks to channels, match channel auto-creation

**Possible phases:**
- Phase 1 (~3 days): Post results to channel, tournament announcements, check-in reminder DMs
- Phase 2 (~5 days): Auto-assign participant roles, temp voice channels for matches
- Phase 3 (~1-2 weeks): Slash commands, Linked Roles, per-org bot installation

**Constraint:** Bot currently operates in ONE guild only. Per-org servers needs OAuth2 bot scope (bigger architectural shift).

---

## Priority Ranking

| Priority | Feature | Effort | Impact |
|----------|---------|--------|--------|
| 1 | VAC ban check on registration | 1 day | High |
| 2 | Seasons/Series | 3-5 days | High |
| 3 | Tournament-level auto-DQ | 1 day | Medium |
| 4 | Demo storage + download | 2-3 days | Medium |
| 5 | VOD linking to matches | 1 day | Medium |
| 6 | Discord channel announcements | 2-3 days | Medium |
| 7 | Achievements | 3-5 days | Medium |
| 8 | LFT marketplace | 3-4 days | Medium |
| 9 | Bracket embed frontend | 2 days | Low |
| 10 | Localization | 2-3 weeks | High (market expansion) |

**Biggest structural gap:** No persistent competitive identity (ELO/MMR + stats + history) outside of leaderboard. Platforms like FACEIT succeed because players have a rank they care about between tournaments.
