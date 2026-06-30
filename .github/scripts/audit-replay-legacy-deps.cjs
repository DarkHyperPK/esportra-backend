#!/usr/bin/env node
/**
 * Audit legacy table dependencies required for CI post-baseline migration replay.
 * Node.js port of audit-replay-legacy-deps.py
 */

const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.resolve(__dirname, '..', '..');
const MIGRATIONS_DIR = path.join(REPO_ROOT, 'src', 'Esportra.Infrastructure', 'Migrations', 'Scripts');
const OUTPUT = path.join(REPO_ROOT, '.github', 'ci', 'replay-legacy-manifest.json');
const BASELINE = '20260317_001_baseline.sql';

const BUILTIN_TABLES = new Set([
  'users', 'identities', 'sessions', 'refresh_tokens', 'mfa_factors',
  'mfa_challenges', 'mfa_amr_claims', 'audit_log_entries', 'flow_state',
  'saml_providers', 'saml_relay_states', 'sso_providers', 'sso_domains',
  'one_time_tokens', 'buckets', 'objects', 's3_multipart_uploads',
  's3_multipart_uploads_parts', 'schemaversions', 'schema_migrations'
]);

const EXTERNAL_SCHEMAS = new Set([
  'auth', 'storage', 'extensions', 'pg_catalog', 'information_schema', 'realtime', 'vault'
]);

function stripNoise(sql) {
  sql = sql.replace(/--[^\n]*/g, '');
  sql = sql.replace(/\/\*[\s\S]*?\*\//g, '');
  sql = sql.replace(/\$(\w*)\$[\s\S]*?\$\1\$/g, '$$');
  sql = sql.replace(/'(?:[^']|'')*'/g, "''");
  return sql;
}

function canonical(schema, table) {
  const s = (schema || '').toLowerCase();
  const t = table.toLowerCase();
  if (EXTERNAL_SCHEMAS.has(s)) return null;
  if (s !== '' && s !== 'public') return null;
  if (BUILTIN_TABLES.has(t)) return null;
  return t;
}

const patterns = {
  CREATE_TABLE: /\bCREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:(\w+)\.)?(\w+)\s*\(/gi,
  ALTER_TABLE: /\bALTER\s+TABLE\s+(?:IF\s+EXISTS\s+)?(?:(\w+)\.)?(\w+)\b/gi,
  CREATE_INDEX: /\bCREATE\s+(?:UNIQUE\s+)?INDEX\s+(?:CONCURRENTLY\s+)?(?:IF\s+NOT\s+EXISTS\s+)?\w+\s+ON\s+(?:(\w+)\.)?(\w+)\b/gi,
  CREATE_POLICY: /\bCREATE\s+(?:OR\s+REPLACE\s+)?POLICY\s+\S+\s+ON\s+(?:(\w+)\.)?(\w+)\b/gi,
  CREATE_TRIGGER: /\bCREATE\s+(?:OR\s+REPLACE\s+)?(?:CONSTRAINT\s+)?TRIGGER\s+\w+\b[\s\S]+?\bON\s+(?:(\w+)\.)?(\w+)\b/gi,
  REFERENCES: /\bREFERENCES\s+(?:(\w+)\.)?(\w+)\s*\(/gi,
  INSERT_INTO: /\bINSERT\s+INTO\s+(?:(\w+)\.)?(\w+)\b/gi,
  UPDATE_TABLE: /\bUPDATE\s+(?:(\w+)\.)?(\w+)\b/gi,
  DELETE_FROM: /\bDELETE\s+FROM\s+(?:(\w+)\.)?(\w+)\b/gi,
  FROM_JOIN: /\b(?:FROM|JOIN)\s+(?:(\w+)\.)?(\w+)\b/gi,
  ON_CONFLICT: /\bON\s+CONFLICT\s*\(([^)]+)\)/gi,
};

const opMap = {
  ALTER_TABLE: 'alter',
  CREATE_INDEX: 'index',
  CREATE_POLICY: 'policy',
  CREATE_TRIGGER: 'trigger',
  REFERENCES: 'references',
  INSERT_INTO: 'insert',
  UPDATE_TABLE: 'update',
  DELETE_FROM: 'delete',
  FROM_JOIN: 'join',
};

function extractMatches(content, pattern) {
  const matches = [];
  let m;
  const re = new RegExp(pattern.source, pattern.flags);
  while ((m = re.exec(content)) !== null) {
    matches.push([m[1] || null, m[2]]);
  }
  return matches;
}

function collectDefinedTables(files) {
  const defined = new Set();
  for (const file of files) {
    const content = stripNoise(fs.readFileSync(file, 'utf8'));
    for (const [schema, table] of extractMatches(content, patterns.CREATE_TABLE)) {
      const name = canonical(schema, table);
      if (name) defined.add(name);
    }
  }
  return defined;
}

function main() {
  const checkMode = process.argv.includes('--check');

  if (!fs.existsSync(MIGRATIONS_DIR)) {
    console.error(`ERROR: Migrations directory not found: ${MIGRATIONS_DIR}`);
    process.exit(1);
  }

  const allFiles = fs.readdirSync(MIGRATIONS_DIR)
    .filter(f => f.endsWith('.sql'))
    .sort()
    .map(f => path.join(MIGRATIONS_DIR, f));

  if (allFiles.length === 0) {
    console.error('ERROR: No migration files found.');
    process.exit(1);
  }

  const baselineIdx = allFiles.findIndex(f => path.basename(f) === BASELINE);
  if (baselineIdx < 0) {
    console.error(`ERROR: Baseline file not found: ${BASELINE}`);
    process.exit(1);
  }

  const postBaseline = allFiles.slice(baselineIdx + 1);
  const definedTables = collectDefinedTables(allFiles);

  const legacyTables = {};
  const allOnConflictCols = new Set();

  for (const file of postBaseline) {
    const filename = path.basename(file);
    const content = stripNoise(fs.readFileSync(file, 'utf8'));

    // Collect ON CONFLICT columns
    for (const m of content.matchAll(patterns.ON_CONFLICT)) {
      const cols = m[1].split(',').map(c => c.trim().toLowerCase());
      cols.forEach(c => allOnConflictCols.add(c));
    }

    for (const [patName, pattern] of Object.entries(patterns)) {
      if (patName === 'CREATE_TABLE' || patName === 'ON_CONFLICT') continue;

      const op = opMap[patName];
      if (!op) continue;

      for (const [schema, table] of extractMatches(content, pattern)) {
        const name = canonical(schema, table);
        if (!name) continue;
        if (definedTables.has(name)) continue;

        if (!legacyTables[name]) {
          legacyTables[name] = {
            firstSeenIn: filename,
            operations: [],
            columnsMentioned: [],
            onConflictColumns: [],
          };
        }
        if (!legacyTables[name].operations.includes(op)) {
          legacyTables[name].operations.push(op);
        }
      }
    }
  }

  // Add onConflictColumns to all legacy tables (mirrors Python behavior)
  const sortedOnConflict = [...allOnConflictCols].sort();
  for (const table of Object.keys(legacyTables)) {
    legacyTables[table].onConflictColumns = sortedOnConflict.filter(c =>
      c !== 'name' || legacyTables[table].operations.includes('insert')
    );
  }

  const sortedLegacy = {};
  for (const key of Object.keys(legacyTables).sort()) {
    sortedLegacy[key] = legacyTables[key];
  }

  const manifest = {
    baseline: BASELINE,
    generatedBy: '.github/scripts/audit-replay-legacy-deps.py',
    postBaselineScriptCount: postBaseline.length,
    legacyTables: sortedLegacy,
  };

  const output = JSON.stringify(manifest, null, 2) + '\n';

  if (checkMode) {
    if (!fs.existsSync(OUTPUT)) {
      console.error(`ERROR: ${OUTPUT} does not exist`);
      process.exit(1);
    }
    const existing = fs.readFileSync(OUTPUT, 'utf8');
    if (existing !== output) {
      console.error(`ERROR: ${OUTPUT} is stale. Run: python .github/scripts/audit-replay-legacy-deps.py`);
      process.exit(1);
    }
    console.log('replay-legacy-manifest.json is up to date');
  } else {
    fs.writeFileSync(OUTPUT, output);
    console.log(`Wrote ${OUTPUT}`);
    console.log(`Post-baseline scripts: ${postBaseline.length}`);
    console.log(`Legacy tables: ${Object.keys(sortedLegacy).length}`);
  }
}

main();
