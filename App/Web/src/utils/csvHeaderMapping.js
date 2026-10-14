// D-225: CSV header auto-detection for import mapping profiles. The admin
// picks a sample CSV; only its first chunk is read, in the browser (nothing
// is uploaded), and each internal field of the profile's feed type is
// matched to one of the file's column headers.
//
// FEED_FIELDS below is the one place the internal fields per feed type and
// their header synonyms are defined (documented in
// Claude_Docs/Design_Risk-Scoring-Import.md, "Header auto-detection"). The
// field names are the ones the import code reads
// (App/Api/Imports/RiskScoringImportService.cs, ResolveField calls); keep the
// two in step when a field is added there.

// Each synonym is compared after normalizeHeader, so "IP Address",
// "ip_address" and "IP-Address" are all the same synonym.
const DESCRIPTION = { name: 'Description', synonyms: ['Desc', 'Notes', 'Comment', 'Comments', 'Details'] }
const GROUP_IDENTIFIER = {
  name: 'GroupIdentifier',
  synonyms: ['Group ID', 'Group Identifier', 'Identifier', 'objectGUID', 'objectSid', 'Group SID', 'Group GUID', 'distinguishedName', 'DN']
}
const ACCOUNT_NAME = { name: 'AccountName', synonyms: ['Account', 'User Name', 'Username', 'User', 'sAMAccountName', 'Member', 'Member Name'] }
const ACCOUNT_KEY = { name: 'AccountKey', synonyms: ['Account ID', 'Account Key'] }
const TARGET_IDENTIFIER_TYPE = { name: 'TargetIdentifierType', required: true, synonyms: ['Identifier Type', 'Target ID Type', 'ID Type'] }
const TARGET_IDENTIFIER_VALUE = {
  name: 'TargetIdentifierValue',
  required: true,
  synonyms: ['Identifier Value', 'Target Identifier', 'Target ID', 'Identifier', 'Target']
}

// The seeded web.dim_target_identifier_type rows, each a TargetInventory
// field of its own. An identifier type added later still gets an exact-name
// match: identifierFields() adds any the API lists that aren't here.
const IDENTIFIER_FIELDS = [
  { name: 'ADGuid', synonyms: ['objectGUID', 'AD GUID', 'Object GUID', 'GUID'] },
  { name: 'ADSid', synonyms: ['objectSid', 'AD SID', 'SID', 'Security Identifier'] },
  { name: 'LdapBaseDN', synonyms: ['Base DN', 'LDAP Base DN', 'Search Base'] },
  { name: 'DatabaseInstanceName', synonyms: ['Instance', 'Instance Name', 'Database Instance', 'DB Instance', 'SQL Instance'] },
  { name: 'ApplianceSerialNumber', synonyms: ['Serial Number', 'Serial', 'Serial No', 'Appliance Serial'] },
  { name: 'LinuxHostSignature', synonyms: ['Host Signature', 'Linux Signature', 'SSH Host Key'] },
  { name: 'FQDN', synonyms: ['DNS Name', 'dNSHostName', 'DNS Host Name', 'Fully Qualified Domain Name'] },
  { name: 'Hostname', synonyms: ['Host Name', 'Host', 'Computer Name', 'Machine Name', 'NetBIOS Name'] },
  { name: 'IPAddress', synonyms: ['IP', 'IP Address', 'IPv4', 'IPv4 Address', 'Address'] }
]

export const FEED_FIELDS = {
  TargetInventory: [
    { name: 'TargetType', required: true, synonyms: ['Type', 'Target Type', 'Asset Type', 'Device Type', 'Kind', 'Category'] },
    { name: 'TargetName', required: true, synonyms: ['Name', 'Target', 'Asset', 'Asset Name', 'Device', 'Device Name', 'Display Name', 'Server Name'] },
    { name: 'RiskScore', required: true, synonyms: ['Risk', 'Score', 'Risk Rating', 'Risk Value'] },
    DESCRIPTION,
    { name: 'DiscoverySource', synonyms: ['Source', 'Discovered By', 'Discovery Tool', 'Scanner'] },
    ...IDENTIFIER_FIELDS
  ],
  AccessGroupInventory: [
    { name: 'GroupName', required: true, synonyms: ['Group', 'Name', 'Group Display Name', 'Display Name', 'cn'] },
    { ...GROUP_IDENTIFIER, required: true },
    { name: 'GroupScope', synonyms: ['Scope', 'Group Scope'] },
    { name: 'BaseRiskScore', required: true, synonyms: ['Risk Score', 'Risk', 'Score', 'Base Score'] },
    DESCRIPTION
  ],
  AccessGroupTargetMap: [{ ...GROUP_IDENTIFIER, required: true }, TARGET_IDENTIFIER_TYPE, TARGET_IDENTIFIER_VALUE],
  // AccountName or AccountKey: either one is enough, so neither is required on its own.
  AccountAccessGroupMembership: [ACCOUNT_NAME, ACCOUNT_KEY, { ...GROUP_IDENTIFIER, required: true }],
  AccountTargetMap: [ACCOUNT_NAME, ACCOUNT_KEY, TARGET_IDENTIFIER_TYPE, TARGET_IDENTIFIER_VALUE]
}

/** The feed type's fields, plus any identifier type the API knows that FEED_FIELDS doesn't (TargetInventory only). */
export function fieldsFor(feedType, identifierTypes = []) {
  const fields = FEED_FIELDS[feedType] ?? []
  if (feedType !== 'TargetInventory') return fields
  const known = new Set(fields.map(f => f.name))
  const extra = identifierTypes.filter(t => !known.has(t)).map(t => ({ name: t, synonyms: [] }))
  return [...fields, ...extra]
}

/** Case, spaces, underscores and punctuation don't count: "IP_Address", "ip address" and "IP-Address" all become "ipaddress". */
export function normalizeHeader(text) {
  return String(text ?? '').toLowerCase().replace(/[^\p{L}\p{N}]/gu, '')
}

/** The delimiter used in the header line: whichever of , ; or tab appears most outside quotes (comma if none). */
export function detectDelimiter(line) {
  const counts = { ',': 0, ';': 0, '\t': 0 }
  let inQuotes = false
  for (const ch of line) {
    if (ch === '"') inQuotes = !inQuotes
    else if (!inQuotes && ch in counts) counts[ch]++
  }
  let best = ','
  for (const d of [';', '\t']) if (counts[d] > counts[best]) best = d
  return best
}

/** The first record of the text: up to the first line break outside quotes (a quoted header may span lines). */
export function firstRecord(text) {
  let inQuotes = false
  for (let i = 0; i < text.length; i++) {
    const ch = text[i]
    if (ch === '"') inQuotes = !inQuotes
    else if (!inQuotes && (ch === '\n' || ch === '\r')) return text.slice(0, i)
  }
  return text
}

/**
 * The column headers in the text's first line: a UTF-8 byte order mark is
 * dropped, the delimiter detected, quoted headers unquoted ("" is a quote),
 * each header trimmed and empty ones left out.
 */
export function parseHeaderLine(text) {
  const line = firstRecord(String(text ?? '').replace(/^﻿/, ''))
  const delimiter = detectDelimiter(line)
  const headers = []
  let current = ''
  let inQuotes = false
  for (let i = 0; i < line.length; i++) {
    const ch = line[i]
    if (inQuotes) {
      if (ch === '"' && line[i + 1] === '"') { current += '"'; i++ }
      else if (ch === '"') inQuotes = false
      else current += ch
    } else if (ch === '"') {
      inQuotes = true
    } else if (ch === delimiter) {
      headers.push(current)
      current = ''
    } else {
      current += ch
    }
  }
  headers.push(current)
  return { delimiter, headers: headers.map(h => h.trim()).filter(h => h !== '') }
}

/** Reads only the first chunk of the file (64 KB by default), so a large export isn't loaded whole. */
export async function readCsvHeaders(file, maxBytes = 65536) {
  const chunk = file.slice(0, maxBytes)
  const text = typeof chunk.text === 'function'
    ? await chunk.text()
    : await new Promise((resolve, reject) => {
      const reader = new FileReader()
      reader.onload = () => resolve(reader.result)
      reader.onerror = () => reject(reader.error)
      reader.readAsText(chunk)
    })
  return parseHeaderLine(text)
}

/**
 * Matches each field to a header: first every exact match (after
 * normalizeHeader), then synonyms in the order listed. A header is used for
 * one field only. Returns the matches, the fields left unmatched and the
 * headers no field used.
 */
export function matchHeaders(headers, fields) {
  const byNormalized = new Map()
  for (const h of headers) {
    const key = normalizeHeader(h)
    if (key && !byNormalized.has(key)) byNormalized.set(key, h)
  }
  const used = new Set()
  const matched = new Map()

  function take(field, candidate, how) {
    const header = byNormalized.get(normalizeHeader(candidate))
    if (header === undefined || used.has(header)) return false
    used.add(header)
    matched.set(field.name, { field: field.name, header, how })
    return true
  }

  for (const field of fields) take(field, field.name, 'exact')
  for (const field of fields) {
    if (matched.has(field.name)) continue
    for (const synonym of field.synonyms ?? []) if (take(field, synonym, 'synonym')) break
  }

  return {
    matches: fields.filter(f => matched.has(f.name)).map(f => matched.get(f.name)),
    unmatchedFields: fields.filter(f => !matched.has(f.name)).map(f => f.name),
    unusedHeaders: headers.filter(h => !used.has(h))
  }
}

/**
 * Applies matches to a profile's field rows ({ targetFieldName,
 * sourceColumnName, ... }) without overwriting what the admin set: a row
 * with no source column is filled in, a field with no row gets a new row,
 * a row already set to the same column is left alone, and a row set to a
 * different column is returned as a conflict for the admin to confirm
 * (applyConflicts). Returns new rows; the input isn't changed.
 */
export function applyMatches(rows, matches, newRow = () => ({ isRequired: false, defaultValue: '' })) {
  const result = rows.map(r => ({ ...r }))
  const filled = []
  const conflicts = []
  for (const m of matches) {
    const row = result.find(r => (r.targetFieldName ?? '').trim().toLowerCase() === m.field.toLowerCase())
    if (!row) {
      result.push({ ...newRow(), targetFieldName: m.field, sourceColumnName: m.header })
      filled.push(m)
    } else if (!(row.sourceColumnName ?? '').trim()) {
      row.sourceColumnName = m.header
      filled.push(m)
    } else if (row.sourceColumnName.trim() !== m.header) {
      conflicts.push({ field: m.field, current: row.sourceColumnName, detected: m.header })
    }
  }
  return { rows: result, filled, conflicts }
}

/** Overwrites the conflicting rows' source column with the detected header -- only after the admin confirms. */
export function applyConflicts(rows, conflicts) {
  return rows.map(r => {
    const c = conflicts.find(x => x.field.toLowerCase() === (r.targetFieldName ?? '').trim().toLowerCase())
    return c ? { ...r, sourceColumnName: c.detected } : r
  })
}
