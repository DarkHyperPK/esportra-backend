#!/usr/bin/env bash
# Upload Rainbow Six Siege map AVIFs to Supabase Storage and seed map_image_url.
# Run on staging host with SUPABASE_URL, SUPABASE_SERVICE_ROLE_KEY, and DATABASE_URL set.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
MANIFEST="${MANIFEST:-$REPO_ROOT/../frag-and-book-main/src/data/r6-map-manifest.json}"
MAPS_DIR="${MAPS_DIR:-$REPO_ROOT/../frag-and-book-main/R6 Maps}"
BUCKET="${BUCKET:-system.assets.games}"
PREFIX="${PREFIX:-r6/maps}"
GAME_NAME="Rainbow Six Siege"

: "${SUPABASE_URL:?Set SUPABASE_URL}"
: "${SUPABASE_SERVICE_ROLE_KEY:?Set SUPABASE_SERVICE_ROLE_KEY}"
: "${DATABASE_URL:?Set DATABASE_URL}"

if [[ ! -f "$MANIFEST" ]]; then
  echo "Manifest not found: $MANIFEST" >&2
  exit 1
fi

if [[ ! -d "$MAPS_DIR" ]]; then
  echo "Maps directory not found: $MAPS_DIR" >&2
  exit 1
fi

uploaded=0
updated=0

while IFS= read -r entry; do
  filename=$(echo "$entry" | jq -r '.filename')
  display_name=$(echo "$entry" | jq -r '.displayName')
  slug=$(echo "$entry" | jq -r '.slug')
  source="$MAPS_DIR/$filename"
  object_path="$PREFIX/$slug.avif"

  if [[ ! -f "$source" ]]; then
    echo "SKIP missing file: $source" >&2
    continue
  fi

  curl -sf -X POST \
    "$SUPABASE_URL/storage/v1/object/$BUCKET/$object_path" \
    -H "Authorization: Bearer $SUPABASE_SERVICE_ROLE_KEY" \
    -H "Content-Type: image/avif" \
    -H "x-upsert: true" \
    --data-binary @"$source" >/dev/null

  public_url="$SUPABASE_URL/storage/v1/object/public/$BUCKET/$object_path"

  rows=$(psql "$DATABASE_URL" -Atc \
    "UPDATE game_maps SET map_image_url = '$public_url' WHERE game = '$GAME_NAME' AND map_name = '$display_name' RETURNING id;")

  if [[ -n "$rows" ]]; then
    updated=$((updated + 1))
    echo "OK  $display_name -> $public_url"
  else
    echo "WARN no DB row for $display_name" >&2
  fi
  uploaded=$((uploaded + 1))
done < <(jq -c '.maps[]' "$MANIFEST")

echo ""
echo "Upload summary: files=$uploaded db_updates=$updated"
psql "$DATABASE_URL" -c \
  "SELECT COUNT(*) AS total, COUNT(map_image_url) AS with_image FROM game_maps WHERE game = '$GAME_NAME';"
