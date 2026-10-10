import { describe, it, expect } from 'vitest'
import { mergeProviderSettings } from './providerSettings'

const samlDefaults = {
  spEntityId: '',
  idpEntityId: '',
  idpSingleSignOnDestination: '',
  groupClaimType: 'http://schemas.xmlsoap.org/claims/Group'
}

describe('mergeProviderSettings', () => {
  it('returns the defaults when nothing is stored, or the JSON is unreadable', () => {
    expect(mergeProviderSettings(samlDefaults, null)).toEqual(samlDefaults)
    expect(mergeProviderSettings(samlDefaults, '{not json')).toEqual(samlDefaults)
    expect(mergeProviderSettings(samlDefaults, '[1,2]')).toEqual(samlDefaults)
  })

  it('maps PascalCase names (the seed script) onto the page\'s camelCase fields', () => {
    const merged = mergeProviderSettings(samlDefaults, JSON.stringify({ SpEntityId: 'urn:bluetrack:sp', IdpEntityId: 'http://www.okta.com/x' }))
    expect(merged).toEqual({ ...samlDefaults, spEntityId: 'urn:bluetrack:sp', idpEntityId: 'http://www.okta.com/x' })
    expect(merged).not.toHaveProperty('SpEntityId')
  })

  it('keeps one copy of a setting stored twice, preferring the filled-in value whichever comes last', () => {
    // The shape found on the dev host: camelCase filled, then PascalCase empty.
    const stored = {
      spEntityId: 'urn:bluetrack:sp',
      idpSingleSignOnDestination: 'https://okta.example.com/sso',
      SpEntityId: '',
      IdpSingleSignOnDestination: '',
      GroupClaimType: 'groups'
    }
    expect(mergeProviderSettings(samlDefaults, JSON.stringify(stored))).toEqual({
      spEntityId: 'urn:bluetrack:sp',
      idpEntityId: '',
      idpSingleSignOnDestination: 'https://okta.example.com/sso',
      groupClaimType: 'groups'
    })
  })

  it('lets a stored empty value clear a default, but not a filled-in duplicate', () => {
    expect(mergeProviderSettings(samlDefaults, '{"groupClaimType":""}').groupClaimType).toBe('')
    expect(mergeProviderSettings(samlDefaults, '{"groupClaimType":"roles","GroupClaimType":""}').groupClaimType).toBe('roles')
  })

  it('keeps settings the page doesn\'t know, deduplicated the same way', () => {
    const merged = mergeProviderSettings(samlDefaults, '{"ExtraSetting":"","extraSetting":"x"}')
    expect(merged.ExtraSetting).toBe('x')
    expect(merged).not.toHaveProperty('extraSetting')
  })
})
