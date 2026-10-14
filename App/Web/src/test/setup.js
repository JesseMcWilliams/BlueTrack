// D-227: Vitest setup (vitest.config.js setupFiles). Every mount() gets the
// app's i18n plugin, and every test starts in English with the browser's
// number/date format -- see src/test/i18n.js.
import { beforeEach } from 'vitest'
import { config } from '@vue/test-utils'
import { i18n, resetI18n } from './i18n'

config.global.plugins = [...(config.global.plugins ?? []), i18n]

beforeEach(() => resetI18n())
