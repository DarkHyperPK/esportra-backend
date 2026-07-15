# BR Pro Lobby Migration — Pre-Deploy Gate

Run `scripts/br-schema-audit.sql` on the **target database** before applying `20260611120000_br_pro_lobby_model.sql`.

## Staging audit (2026-06-11)

- Host: `ubuntu@79.72.48.169`
- Container: `supabase-db-usoocgow4s0wow00gsw04kcg`
- `br_groups`: 0 | `br_rounds`: 0 | `br_round_results`: 0 | BR stages: 0
- Schema matches repo migrations (map, queue_timer, solo participant_id, active-round unique index)

## Production gate

1. Confirm which Supabase DB container the production API uses (`supabase-db-usoocgow4s0wow00gsw04kcg` vs `supabase-db-moooksg0ssk04cg8coksgowc`).
2. Run `br-schema-audit.sql` on that container.
3. If any BR row counts > 0, verify backfill on staging replay before prod.
4. Deploy backend migration + API + frontend in **one release** (no proxy layer).

## SSH one-liner

```bash
ssh -i C:/Users/Mudassir/Downloads/ssh-key-2026-04-04.key ubuntu@79.72.48.169 \
  "sudo docker exec -i supabase-db-usoocgow4s0wow00gsw04kcg psql -U supabase_admin -d postgres" \
  < scripts/br-schema-audit.sql
```
