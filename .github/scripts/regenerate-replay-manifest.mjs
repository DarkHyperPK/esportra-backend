import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = path.resolve(__dirname, '../..');
const MIGRATIONS_DIR = path.join(
  REPO_ROOT,
  'src/Esportra.Infrastructure/Migrations/Scripts',
);
const OUTPUT = path.join(REPO_ROOT, '.github/ci/replay-legacy-manifest.json');
const BASELINE = '20260317_001_baseline.sql';

const BUILTIN_TABLES = new Set([
  'users', 'identities', 'sessions', 'refresh_tokens', 'mfa_factors', 'mfa_challenges',
  'mfa_amr_claims', 'audit_log_entries', 'flow_state', 'saml_providers', 'saml_relay_states',
  'sso_providers', 'sso_domains', 'one_time_tokens', 'buckets', 'objects',
  's3_multipart_uploads', 's3_multipart_uploads_parts', 'schemaversions', 'schema_migrations',
]);
const EXTERNAL_SCHEMAS = new Set([
  'auth', 'storage', 'extensions', 'pg_catalog', 'information_schema', 'realtime', 'vault',
]);
const SKIP_TABLES = new Set([
  'pg_policies', 'pg_constraint', 'pg_enum', 'pg_publication', 'pg_publication_tables',
  'pg_roles', 'pg_class', 'pg_namespace', 'numbered', 'existing', 'excluded', 'new', 'old',
]);

function stripNoise(sql) {
  return sql
    .replace(/--[^\n]*/g, '')
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .replace(/\$(\w*)\$[\s\S]*?\$\1\$/g, '$$')
    .replace(/'(?:[^']|'')*'/g, "''");
}

function canonical(schema, table) {
  const s = (schema ?? '').toLowerCase();
  const t = table.toLowerCase();
  if (EXTERNAL_SCHEMAS.has(s)) return null;
  if (s !== '' && s !== 'public') return null;
  if (BUILTIN_TABLES.has(t)) return null;
  return t;
}

function extractMatches(content, regex) {
  const rows = [];
  const re = new RegExp(regex.source, regex.flags);
  let match;
  while ((match = re.exec(content)) !== null) {
    rows.push([match[1], match[2]]);
  }
  return rows;
}

function collectDefinedTables(files) {
  const defined = new Set();
  const createTable = /\bCREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:(\w+)\.)?(\w+)\s*\(/gi;
  for (const file of files) {
    const content = stripNoise(fs.readFileSync(file, 'utf8'));
    for (const [schema, table] of extractMatches(content, createTable)) {
      const name = canonical(schema, table);
      if (name) defined.add(name);
    }
  }
  return defined;
}

function postBaselineFiles(files) {
  return files.filter((file) => path.basename(file) > BASELINE);
}

function recordTable(entries, table, migration, operation) {
  if (SKIP_TABLES.has(table)) return;
  const entry = entries[table] ?? {
    firstSeenIn: migration,
    operations: [],
    columnsMentioned: [],
    onConflictColumns: [],
  };
  if (migration < entry.firstSeenIn) entry.firstSeenIn = migration;
  if (!entry.operations.includes(operation)) entry.operations.push(operation);
  entries[table] = entry;
}

function auditLegacyTables(files) {
  const defined = collectDefinedTables(files);
  const entries = {};
  const referencePatterns = [
    [/\bALTER\s+TABLE\s+(?:IF\s+EXISTS\s+)?(?:(\w+)\.)?(\w+)\b/gi, 'alter'],
    [/\bCREATE\s+(?:UNIQUE\s+)?INDEX\s+(?:CONCURRENTLY\s+)?(?:IF\s+NOT\s+EXISTS\s+)?\w+\s+ON\s+(?:(\w+)\.)?(\w+)\b/gi, 'index'],
    [/\bCREATE\s+(?:OR\s+REPLACE\s+)?POLICY\s+\S+\s+ON\s+(?:(\w+)\.)?(\w+)\b/gi, 'policy'],
    [/\bCREATE\s+(?:OR\s+REPLACE\s+)?(?:CONSTRAINT\s+)?TRIGGER\s+\w+\b[\s\S]*?\bON\s+(?:(\w+)\.)?(\w+)\b/gi, 'trigger'],
    [/\bREFERENCES\s+(?:(\w+)\.)?(\w+)\s*\(/gi, 'references'],
    [/\bINSERT\s+INTO\s+(?:(\w+)\.)?(\w+)\b/gi, 'insert'],
    [/\bUPDATE\s+(?:(\w+)\.)?(\w+)\b/gi, 'update'],
    [/\bDELETE\s+FROM\s+(?:(\w+)\.)?(\w+)\b/gi, 'delete'],
    [/\b(?:FROM|JOIN)\s+(?:(\w+)\.)?(\w+)\b/gi, 'join'],
  ];

  for (const file of postBaselineFiles(files)) {
    const content = stripNoise(fs.readFileSync(file, 'utf8'));
    const migration = path.basename(file);

    for (const [regex, operation] of referencePatterns) {
      for (const [schema, table] of extractMatches(content, regex)) {
        const name = canonical(schema, table);
        if (name && !defined.has(name)) {
          recordTable(entries, name, migration, operation);
        }
      }
    }

    const onConflict = /\bON\s+CONFLICT\s*\(([^)]+)\)/gi;
    let conflictMatch;
    while ((conflictMatch = onConflict.exec(content)) !== null) {
      const cols = conflictMatch[1]
        .split(',')
        .map((col) => col.trim().replace(/^"|"$/g, ''));
      for (const entry of Object.values(entries)) {
        for (const col of cols) {
          if (col && !entry.onConflictColumns.includes(col)) {
            entry.onConflictColumns.push(col);
          }
        }
      }
    }
  }

  for (const entry of Object.values(entries)) {
    entry.operations.sort();
    entry.columnsMentioned.sort();
    entry.onConflictColumns.sort();
  }

  return Object.fromEntries(Object.entries(entries).sort(([a], [b]) => a.localeCompare(b)));
}

const files = fs.readdirSync(MIGRATIONS_DIR)
  .filter((name) => name.endsWith('.sql'))
  .sort()
  .map((name) => path.join(MIGRATIONS_DIR, name));

const manifest = {
  baseline: BASELINE,
  generatedBy: '.github/scripts/audit-replay-legacy-deps.py',
  postBaselineScriptCount: postBaselineFiles(files).length,
  legacyTables: auditLegacyTables(files),
};

fs.writeFileSync(OUTPUT, `${JSON.stringify(manifest, null, 2)}\n`, 'utf8');
console.log(
  `Wrote ${OUTPUT} (${Object.keys(manifest.legacyTables).length} legacy table(s), `
  + `${manifest.postBaselineScriptCount} post-baseline scripts)`,
);
