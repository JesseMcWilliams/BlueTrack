import { describe, it, expect } from 'vitest'
import {
  parseHeaderLine, detectDelimiter, normalizeHeader, matchHeaders, applyMatches, applyConflicts,
  fieldsFor, readCsvHeaders, FEED_FIELDS
} from './csvHeaderMapping'

// D-225: CSV header auto-detection for import mapping profiles.
describe('parseHeaderLine', () => {
  it('drops a UTF-8 byte order mark and reads only the first line', () => {
    expect(parseHeaderLine('﻿TargetName,RiskScore\r\nweb01,5\r\n')).toEqual({ delimiter: ',', headers: ['TargetName', 'RiskScore'] })
  })

  it('unquotes quoted headers, keeping delimiters, doubled quotes and line breaks inside them', () => {
    const { headers } = parseHeaderLine('"Name, full","Say ""hi""","Two\nlines",Plain\nrow')
    expect(headers).toEqual(['Name, full', 'Say "hi"', 'Two\nlines', 'Plain'])
  })

  it('detects semicolon and tab delimiters from the header line', () => {
    expect(parseHeaderLine('Name;Type;Score\n1;2;3')).toEqual({ delimiter: ';', headers: ['Name', 'Type', 'Score'] })
    expect(parseHeaderLine('Name\tType\tScore')).toEqual({ delimiter: '\t', headers: ['Name', 'Type', 'Score'] })
  })

  it('ignores delimiters inside quotes when detecting', () => {
    expect(detectDelimiter('"a;b;c;d",e,f')).toBe(',')
    expect(detectDelimiter('single')).toBe(',')
  })

  it('trims headers and leaves out empty ones', () => {
    expect(parseHeaderLine(' Name , ,Type,').headers).toEqual(['Name', 'Type'])
  })
})

describe('readCsvHeaders', () => {
  it('reads only the first chunk of a large file', async () => {
    const big = 'Name,Type\n' + 'x,y\n'.repeat(100000)
    const file = new File([big], 'big.csv', { type: 'text/csv' })
    let sliced = null
    const original = file.slice.bind(file)
    file.slice = (start, end) => { sliced = [start, end]; return original(start, end) }
    expect((await readCsvHeaders(file, 1024)).headers).toEqual(['Name', 'Type'])
    expect(sliced).toEqual([0, 1024])
  })
})

describe('matchHeaders', () => {
  it('ignores case, spaces, underscores and punctuation', () => {
    expect(normalizeHeader(' IP_Address-(v4) ')).toBe('ipaddressv4')
    const { matches } = matchHeaders(['target_name', 'RISK SCORE', 'Target-Type'], fieldsFor('TargetInventory'))
    expect(matches.map(m => [m.field, m.header, m.how])).toEqual([
      ['TargetType', 'Target-Type', 'exact'],
      ['TargetName', 'target_name', 'exact'],
      ['RiskScore', 'RISK SCORE', 'exact']
    ])
  })

  it('falls back to synonyms, exact matches first, each header used once', () => {
    const { matches, unmatchedFields, unusedHeaders } = matchHeaders(
      ['Name', 'Kind', 'Score', 'IP Address', 'objectGUID', 'Hostname', 'Owner'], fieldsFor('TargetInventory'))
    const byField = Object.fromEntries(matches.map(m => [m.field, m]))
    expect(byField.TargetName).toMatchObject({ header: 'Name', how: 'synonym' })
    expect(byField.TargetType).toMatchObject({ header: 'Kind', how: 'synonym' })
    expect(byField.RiskScore).toMatchObject({ header: 'Score', how: 'synonym' })
    expect(byField.IPAddress).toMatchObject({ header: 'IP Address', how: 'exact' }) // same name once normalized
    expect(byField.ADGuid).toMatchObject({ header: 'objectGUID', how: 'synonym' })
    expect(byField.Hostname).toMatchObject({ header: 'Hostname', how: 'exact' })
    expect(unmatchedFields).toContain('Description')
    expect(unusedHeaders).toEqual(['Owner'])
    expect(new Set(matches.map(m => m.header)).size).toBe(matches.length)
  })

  it('adds identifier types the API knows but the list does not', () => {
    const fields = fieldsFor('TargetInventory', ['Hostname', 'CloudResourceId'])
    expect(fields.filter(f => f.name === 'Hostname')).toHaveLength(1)
    expect(matchHeaders(['cloud_resource_id'], fields).matches).toEqual([{ field: 'CloudResourceId', header: 'cloud_resource_id', how: 'exact' }])
  })

  it('defines fields for every feed type the profile page offers', () => {
    expect(Object.keys(FEED_FIELDS)).toEqual(['TargetInventory', 'AccessGroupInventory', 'AccessGroupTargetMap', 'AccountAccessGroupMembership', 'AccountTargetMap'])
    expect(matchHeaders(['User Name', 'Group ID'], fieldsFor('AccountAccessGroupMembership')).matches.map(m => m.field))
      .toEqual(['AccountName', 'GroupIdentifier'])
  })
})

describe('applyMatches', () => {
  const matches = [
    { field: 'TargetName', header: 'Name', how: 'synonym' },
    { field: 'RiskScore', header: 'Score', how: 'synonym' },
    { field: 'TargetType', header: 'Kind', how: 'synonym' },
    { field: 'FQDN', header: 'DNS Name', how: 'synonym' }
  ]

  it('fills empty rows and adds missing fields, never overwriting a set mapping', () => {
    const rows = [
      { targetFieldName: 'TargetName', sourceColumnName: '', isRequired: true, defaultValue: '' },
      { targetFieldName: 'RiskScore', sourceColumnName: 'Criticality', isRequired: false, defaultValue: '0' },
      { targetFieldName: 'TargetType', sourceColumnName: 'Kind', isRequired: false, defaultValue: '' }
    ]
    const { rows: result, filled, conflicts } = applyMatches(rows, matches)

    expect(result[0]).toEqual({ targetFieldName: 'TargetName', sourceColumnName: 'Name', isRequired: true, defaultValue: '' })
    expect(result[1].sourceColumnName).toBe('Criticality')
    expect(result[3]).toEqual({ targetFieldName: 'FQDN', sourceColumnName: 'DNS Name', isRequired: false, defaultValue: '' })
    expect(filled.map(m => m.field)).toEqual(['TargetName', 'FQDN'])
    expect(conflicts).toEqual([{ field: 'RiskScore', current: 'Criticality', detected: 'Score' }])
    expect(rows[0].sourceColumnName).toBe('') // input left alone
  })

  it('replaces only after applyConflicts', () => {
    const rows = [{ targetFieldName: 'RiskScore', sourceColumnName: 'Criticality' }]
    const { conflicts } = applyMatches(rows, matches)
    expect(applyConflicts(rows, conflicts)).toEqual([{ targetFieldName: 'RiskScore', sourceColumnName: 'Score' }])
  })
})
