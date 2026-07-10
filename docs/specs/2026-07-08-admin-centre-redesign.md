# Admin Centre Redesign

## Context

The admin centre is disconnected from actual platform logic. Key problems:
- License management shows incomplete user data (6+ fields stored but not displayed)
- Features like "Feature Tournament" button exist in UI but admins can't verify they work
- 15+ scattered tools with no logical grouping or cross-linking
- No unified dashboard showing what needs attention
- Missing features have permissions defined but no implementation (ghost mode, feature flags, broadcast)

This redesign addresses all issues with a foundation-first approach.

## Approach

**Foundation-First (Approach B):** Fix structural problems first, then fill in data/features.

1. Navigation Architecture (1 week)
2. Data Layer Fixes (1 week)  
3. Dashboard / Command Centre (3-4 days)
4. New Features (1 week)

---

## 1. Navigation Architecture

### Current State

15+ tools under flat `/admin/tools/` structure with no logical organization.

### Proposed Structure

```
/admin
├── dashboard                    # Command Centre
│
├── users/                       # People & Access
│   ├── list                     # User Management
│   ├── [userId]                 # User Detail (dedicated page)
│   ├── verifications            # Verification System
│   ├── licenses                 # License Management
│   └── sessions                 # Session Management
│
├── content/                     # Platform Content
│   ├── tournaments              # Tournament Management
│   ├── teams                    # Team Management
│   ├── venues                   # Venue Management
│   ├── games                    # Game Catalog
│   └── moderation               # Content Moderation
│
├── partners/                    # Business Partners
│   └── sponsors                 # Sponsor Management
│
├── operations/                  # Day-to-day Ops
│   ├── disputes                 # Dispute Management
│   ├── alerts                   # Alerts Management
│   ├── reports                  # Scheduled Reports
│   └── broadcast                # Broadcast Notifications (new)
│
├── system/                      # Platform Config
│   ├── feature-flags            # Feature Flags (new)
│   ├── settings                 # System Settings
│   ├── kill-switches            # Kill Switch Config
│   └── anomalies                # Anomaly Detection
│
├── security/                    # Security & Compliance
│   ├── roles                    # Role Builder
│   ├── admins                   # Admin Access
│   ├── ip-allowlist             # IP Allowlist
│   ├── audit-logs               # Audit Logs
│   └── gdpr                     # GDPR Compliance
│
└── analytics/                   # Insights
    └── overview                 # Analytics Dashboard
```

### Sidebar Design

- Collapsible groups with icons
- Badge counts for pending items (verifications, alerts, etc.)
- Role-aware: hide sections user can't access
- Persist collapsed state in localStorage

### Entity Cross-Linking System

Reusable `<EntityLink type="user|tournament|team|..." id={id} />` component:
- Renders clickable chip with entity name/avatar
- Opens slide-over panel with quick preview
- Actions: View Full, Ghost Mode (users), related actions

Backend support: `GET /api/admin/entities/{type}/{id}/preview` for minimal preview data.

---

## 2. Data Layer Fixes

### Verification Requests - Missing Fields

**Problem:** User submits 15+ fields, admin sees only 9.

**Fields to add to `GET /api/admin/verification-requests`:**

| Field | Type | Description |
|-------|------|-------------|
| `experience_description` | string | Applicant's relevant experience |
| `website_url` | string | Business/personal website |
| `phone` | string | Contact phone number |
| `date_of_birth` | string | Applicant DOB |
| `organizer_data` | object | JSON blob with organizer-specific data |
| `venue_data` | object | JSON blob with venue-specific data |

**Organizer Data Contents:**
- years_experience, staff_count, expected_tournaments_per_month
- streaming_capabilities, previous_tournaments, prize_pool_experience
- team_size_experience, equipment_available
- social_media_links (twitter, discord, instagram)

**Venue Data Contents:**
- total_pcs, pc_specs, operating_hours, hourly_rate
- streaming_setup, tournament_capability
- images (exterior, interior, gaming_area)

### Updated Verification Modal

Three tabs: Details | Experience | Documents

**Details tab:** Personal info, contact, business details
**Experience tab:** Organizer/venue specific data from JSON blobs
**Documents tab:** CNIC images, venue photos

### Other Endpoint Fixes

| Endpoint | Add Fields |
|----------|------------|
| `GET /api/admin/users` | steam_tag, riot_tag |
| `GET /api/admin/licenses` | phone, website_url (JOIN verification_requests) |
| `GET /api/admin/tournaments` | is_featured in list response |

---

## 3. Dashboard / Command Centre

### Purpose

Single screen showing what needs admin attention right now.

### Layout

```
┌─────────────────────────────────────────────────────────────────┐
│  COMMAND CENTRE                                                 │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  ┌─ NEEDS ATTENTION ───────────────────────────────────────┐   │
│  │  [5] Verifications  [2] Disputes  [3] Alerts  [12] Mod  │   │
│  │      (2 stale)          (1 high)      (1 crit)          │   │
│  └─────────────────────────────────────────────────────────┘   │
│                                                                 │
│  ┌─ QUICK STATS ────────┐  ┌─ RECENT ACTIVITY ─────────────┐   │
│  │  Users: 12,847 (+142)│  │  14:28 Ali approved license   │   │
│  │  Tournaments: 234    │  │  14:15 Anomaly detected       │   │
│  │  Active Now: 847     │  │  13:58 Dispute resolved       │   │
│  │  Revenue: $45.2K     │  │  ...                          │   │
│  └──────────────────────┘  └────────────────────────────────┘   │
│                                                                 │
│  ┌─ OLDEST PENDING ────────────────────────────────────────┐   │
│  │  Verification cards with quick approve action           │   │
│  └─────────────────────────────────────────────────────────┘   │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

### New Endpoint

`GET /api/admin/command-centre` - aggregates all dashboard data in one call:

```json
{
  "pending_counts": {
    "verifications": 5,
    "verifications_stale": 2,
    "disputes": 2,
    "disputes_high_priority": 1,
    "alerts": 3,
    "alerts_critical": 1,
    "moderation": 12,
    "gdpr": 1
  },
  "stats": { ... },
  "signups_7d": [...],
  "recent_activity": [...],
  "oldest_pending": [...]
}
```

### Real-time Updates

Subscribe to admin SignalR hub for live count updates without refresh.

### Role-Aware Widgets

Filter widgets based on logged-in admin's permissions. Moderators don't see GDPR/system alerts.

---

## 4. New Features

### 4A. Feature Flags

Full feature flag system with evaluation hierarchy:

1. Kill switch (instant off)
2. User override (specific users)
3. Segment rules (role, country, date filters)
4. Percentage rollout (deterministic hash-based)
5. Default value

**Database Schema:**

```sql
CREATE TABLE feature_flags (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    key TEXT UNIQUE NOT NULL,
    name TEXT NOT NULL,
    description TEXT,
    flag_type TEXT NOT NULL DEFAULT 'boolean',
    default_value JSONB NOT NULL,
    is_enabled BOOLEAN DEFAULT true,
    created_by UUID REFERENCES profiles(id),
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now()
);

CREATE TABLE feature_flag_rules (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    flag_id UUID REFERENCES feature_flags(id) ON DELETE CASCADE,
    priority INT NOT NULL DEFAULT 0,
    conditions JSONB NOT NULL,
    value JSONB NOT NULL,
    percentage INT,
    created_at TIMESTAMPTZ DEFAULT now()
);

CREATE TABLE feature_flag_overrides (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    flag_id UUID REFERENCES feature_flags(id) ON DELETE CASCADE,
    user_id UUID REFERENCES profiles(id) ON DELETE CASCADE,
    value JSONB NOT NULL,
    reason TEXT,
    created_by UUID REFERENCES profiles(id),
    expires_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ DEFAULT now(),
    UNIQUE(flag_id, user_id)
);
```

**Percentage Rollout:** Uses deterministic hash `hash(user_id + flag_key) % 100 < percentage` so same user always gets consistent result.

**API Endpoints:**

- `GET/POST /api/admin/feature-flags` - List/create flags
- `PUT/DELETE /api/admin/feature-flags/{id}` - Update/delete
- `GET/POST/PUT/DELETE /api/admin/feature-flags/{id}/rules` - Manage rules
- `GET/POST/DELETE /api/admin/feature-flags/{id}/overrides` - Manage overrides
- `GET /api/feature-flags/evaluate` - Client: get evaluated flags

**Integration:** FeatureFlagMiddleware adds evaluated flags to HttpContext for endpoint use.

### 4B. Broadcast Notifications

Modular channel-agnostic system. Start with in-app, easily extensible.

**Templates:** Reusable message templates for common broadcasts (maintenance, welcome, announcements). Admins select a template and customize rather than writing from scratch.

**Database Schema:**

```sql
CREATE TABLE broadcast_templates (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name TEXT NOT NULL,
    title TEXT NOT NULL,
    content TEXT NOT NULL,
    content_html TEXT,
    broadcast_type TEXT NOT NULL,
    created_at TIMESTAMPTZ DEFAULT now()
);

CREATE TABLE broadcasts (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    title TEXT NOT NULL,
    content TEXT NOT NULL,
    content_html TEXT,
    broadcast_type TEXT NOT NULL,
    priority TEXT DEFAULT 'normal',
    
    target_type TEXT NOT NULL DEFAULT 'all',
    target_segment JSONB,
    target_user_ids UUID[],
    
    channels TEXT[] NOT NULL DEFAULT '{in_app}',
    
    status TEXT NOT NULL DEFAULT 'draft',
    scheduled_at TIMESTAMPTZ,
    sent_at TIMESTAMPTZ,
    
    total_recipients INT DEFAULT 0,
    delivered_count INT DEFAULT 0,
    read_count INT DEFAULT 0,
    
    created_by UUID REFERENCES profiles(id),
    created_at TIMESTAMPTZ DEFAULT now()
);

CREATE TABLE broadcast_deliveries (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    broadcast_id UUID REFERENCES broadcasts(id) ON DELETE CASCADE,
    user_id UUID REFERENCES profiles(id) ON DELETE CASCADE,
    channel TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'pending',
    delivered_at TIMESTAMPTZ,
    read_at TIMESTAMPTZ,
    error_message TEXT,
    UNIQUE(broadcast_id, user_id, channel)
);
```

**Channel Interface:**

```csharp
public interface IBroadcastChannel
{
    string ChannelName { get; }
    Task<DeliveryResult> DeliverAsync(Broadcast broadcast, Guid userId);
}
```

Start with `InAppBroadcastChannel`. Add Email/Push later by implementing interface.

**API Endpoints:**

- `GET/POST /api/admin/broadcasts` - List/create
- `PUT/DELETE /api/admin/broadcasts/{id}` - Update/delete draft
- `POST /api/admin/broadcasts/{id}/send` - Send now
- `POST /api/admin/broadcasts/{id}/cancel` - Cancel scheduled
- `GET /api/admin/broadcasts/{id}/stats` - Delivery stats
- `GET/POST /api/admin/broadcast-templates` - Manage templates

### 4C. Ghost Mode (User Impersonation)

Secure read-only impersonation with full audit trail.

**Security Layers:**

1. **Permission Gate:** Only admins with `users:impersonate`
2. **JIT Approval:** Non-super_admin must request approval from super_admin
3. **Data Masking:** Sensitive fields masked by default, unmasking requires `users:impersonate:full`
4. **Strict Audit Trail:** Every session, page view, and unmask logged

**Database Schema:**

```sql
CREATE TABLE ghost_approvals (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    requester_id UUID NOT NULL REFERENCES profiles(id),
    target_user_id UUID NOT NULL REFERENCES profiles(id),
    reason TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'pending',
    approved_by UUID REFERENCES profiles(id),
    approved_at TIMESTAMPTZ,
    denial_reason TEXT,
    expires_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ DEFAULT now()
);

CREATE TABLE ghost_sessions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    admin_id UUID NOT NULL REFERENCES profiles(id),
    target_user_id UUID NOT NULL REFERENCES profiles(id),
    approval_id UUID REFERENCES ghost_approvals(id),
    token_hash TEXT NOT NULL,
    reason TEXT NOT NULL,
    
    admin_ip INET,
    admin_user_agent TEXT,
    
    started_at TIMESTAMPTZ DEFAULT now(),
    expires_at TIMESTAMPTZ NOT NULL,
    ended_at TIMESTAMPTZ,
    last_activity_at TIMESTAMPTZ,
    
    pages_viewed JSONB DEFAULT '[]',
    fields_unmasked JSONB DEFAULT '[]',
    
    created_at TIMESTAMPTZ DEFAULT now()
);

CREATE TABLE ghost_data_access_log (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id UUID NOT NULL REFERENCES ghost_sessions(id),
    field_path TEXT NOT NULL,
    was_unmasked BOOLEAN NOT NULL,
    unmasked_reason TEXT,
    accessed_at TIMESTAMPTZ DEFAULT now()
);
```

**Masking Rules:**

| Field Pattern | Mask Type | Requires Full Access |
|---------------|-----------|---------------------|
| wallet.balance | Currency mask | Yes |
| wallet.transactions | Hidden | Yes |
| payment_methods | Card mask (•••• 4242) | Yes |
| profile.phone | Phone mask (+92 ••• •••1234) | No |
| profile.email | Email mask | No |
| profile.date_of_birth | Hidden | Yes |
| connected_accounts.*.token | Hidden | Yes |
| verification.cnic_* | Hidden | Yes |

**API Flow:**

Non-super admin:
1. `POST /api/admin/ghost/request` - Request approval
2. Wait for super_admin approval
3. `POST /api/admin/ghost/{userId}` - Start session with approval_id

Super admin:
1. `POST /api/admin/ghost/{userId}` - Direct access (still logged)

**Constraint:** Ghost mode is READ-ONLY. All mutations blocked.

---

## 5. Implementation Order

### Phase 1: Navigation Architecture (Week 1)

**Backend:**
- [ ] Add `GET /api/admin/entities/{type}/{id}/preview` endpoint
- [ ] Add `is_featured` to tournament list response

**Frontend:**
- [ ] Create new route structure under `/admin/`
- [ ] Build collapsible sidebar with badge counts
- [ ] Create `<EntityLink>` component with slide-over panel
- [ ] Create entity preview components (UserPreview, TournamentPreview, etc.)
- [ ] Migrate existing pages to new routes (with redirects)

### Phase 2: Data Layer Fixes (Week 2)

**Backend:**
- [ ] Update `GET /api/admin/verification-requests` to include all missing fields
- [ ] Update `GET /api/admin/licenses` with verification data JOIN
- [ ] Update `GET /api/admin/users` to include gaming tags

**Frontend:**
- [ ] Redesign verification modal with 3 tabs
- [ ] Add organizer_data display (experience, socials, etc.)
- [ ] Add venue_data display (specs, images, etc.)
- [ ] Update license list with richer context

### Phase 3: Dashboard (Days 3-4 of Week 2)

**Backend:**
- [ ] Create `GET /api/admin/command-centre` aggregated endpoint
- [ ] Add SignalR admin hub events for count updates

**Frontend:**
- [ ] Build Command Centre page
- [ ] Implement "Needs Attention" widget with click-through
- [ ] Implement Quick Stats with sparklines
- [ ] Implement Recent Activity feed
- [ ] Implement Oldest Pending with quick actions
- [ ] Add role-based widget filtering
- [ ] Connect SignalR for real-time updates

### Phase 4: New Features (Week 3)

**4A. Feature Flags:**
- [ ] Create migration for feature_flags, rules, overrides tables
- [ ] Implement FeatureFlagService with evaluation logic
- [ ] Implement FeatureFlagMiddleware
- [ ] Create admin CRUD endpoints
- [ ] Create client evaluation endpoint
- [ ] Build admin UI (list, create/edit, rules, overrides)

**4B. Broadcast:**
- [ ] Create migration for broadcasts, templates, deliveries tables
- [ ] Implement IBroadcastChannel interface
- [ ] Implement InAppBroadcastChannel
- [ ] Implement BroadcastBackgroundService for scheduled sends
- [ ] Create admin CRUD endpoints
- [ ] Create user notification endpoints
- [ ] Build admin UI (list, create/edit, templates, stats)

**4C. Ghost Mode:**
- [ ] Create migration for ghost_approvals, sessions, access_log tables
- [ ] Implement GhostModeMiddleware with masking
- [ ] Implement JIT approval flow
- [ ] Create admin endpoints (request, approve, start, end, audit)
- [ ] Build admin UI (request approval, session management, audit view)
- [ ] Build user-facing ghost banner component

---

## Verification

### Phase 1 Verification
- Navigate through new sidebar structure
- Click entity links and verify slide-over panels open
- Check badge counts update correctly
- Verify old URLs redirect properly

### Phase 2 Verification
- Open verification request detail - all fields visible
- Check organizer_data renders (experience, socials)
- Check venue_data renders (specs, images)
- Verify license list shows enriched data

### Phase 3 Verification
- Load dashboard - all widgets populated
- Click "Needs Attention" items - navigate to correct pages
- Approve a verification - watch count decrease in real-time
- Check role-limited admin sees filtered widgets

### Phase 4 Verification

**Feature Flags:**
- Create flag with percentage rollout
- Add segment rule (organizer only)
- Add user override
- Verify evaluation returns correct values
- Test kill switch overrides everything

**Broadcast:**
- Create broadcast targeting organizers
- Send immediately - verify in-app notification appears
- Schedule broadcast - verify sends at scheduled time
- Check delivery stats

**Ghost Mode:**
- Non-super admin requests approval
- Super admin approves
- Start ghost session - verify read-only
- Verify sensitive fields masked
- Check audit log captures all activity
- Super admin ghosts directly - verify logged
