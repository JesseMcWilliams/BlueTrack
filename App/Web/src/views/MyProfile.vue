<script setup>
// Self-service "Reload My Rights" (D-14) -- re-resolves group membership
// live via POST /api/me/reload-rights and updates the shared rights store
// every permission-aware control on the page reads from. Also the theme
// picker (Design_Accessibility-And-Theming.md, D-93) -- confirmed location
// for this per-user preference, alongside the other self-service settings.
// D-227: and the language picker, which lists only the registered locales
// (src/i18n/index.js SUPPORTED_LOCALES), each by its own name.
import { ref, onMounted } from 'vue'
import { useRightsStore } from '../stores/rights'
import { useThemeStore } from '../stores/theme'
import { useLocaleStore, languageName } from '../stores/locale'
import { usePageSizeStore, PAGE_SIZE_OPTIONS } from '../stores/pageSize'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const rights = useRightsStore()
const theme = useThemeStore()
const locale = useLocaleStore()
const pageSize = usePageSizeStore()

// UI review W12-3: a preference applies here at once; say whether the server kept it too.
const prefStatus = ref(null) // null | { ok: boolean }
async function savePreference(action) {
  prefStatus.value = null
  prefStatus.value = { ok: (await action()) !== false }
}

// UI review W12-2: Reload My Rights keeps the page, and says what happened beside the button.
const reloaded = ref(false)
async function reloadRights() {
  reloaded.value = false
  await rights.reload()
  reloaded.value = !rights.error
}

onMounted(() => rights.ensureLoaded())
</script>

<template>
  <div>
    <h1>{{ t('shell.myProfile.title') }}</h1>
    <p v-if="!rights.loaded && rights.loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="!rights.loaded && rights.error" role="alert">{{ rights.error }}</p>
    <template v-else>
      <dl>
        <dt>{{ t('common.name') }}</dt>
        <dd>{{ rights.displayName }}</dd>
        <dt>{{ t('shell.myProfile.roles') }}</dt>
        <dd>{{ rights.roleNames.join(', ') || t('shell.myProfile.noneMapped') }}</dd>
      </dl>
      <h2>{{ t('shell.myProfile.permissions') }}</h2>
      <table v-if="rights.permissionNames.length > 0">
        <thead>
          <tr><th>{{ t('shell.myProfile.permission') }}</th></tr>
        </thead>
        <tbody>
          <tr v-for="name in rights.permissionNames" :key="name">
            <td>{{ name }}</td>
          </tr>
        </tbody>
      </table>
      <p v-else>{{ t('common.none') }}</p>
      <button :disabled="rights.loading" @click="reloadRights">{{ t('shell.myProfile.reloadRights') }}</button>
      <span v-if="rights.loading" role="status">{{ ' ' + t('common.loading') }}</span>
      <span v-else-if="reloaded" role="status">{{ ' ' + t('shell.myProfile.rightsReloaded') }}</span>
      <span v-if="rights.loaded && rights.error" role="alert">{{ ' ' + rights.error }}</span>
      <p><small>{{ t('shell.myProfile.reloadRightsHint') }}</small></p>

      <h2>{{ t('shell.myProfile.theme') }}</h2>
      <fieldset>
        <legend>{{ t('shell.myProfile.themeLegend') }}</legend>
        <p>
          <label><input type="radio" name="theme" value="Light" :checked="theme.current === 'Light'" @change="savePreference(() => theme.setTheme('Light'))" /> {{ t('shell.myProfile.themeLight') }}</label>
        </p>
        <p>
          <label><input type="radio" name="theme" value="Dark" :checked="theme.current === 'Dark'" @change="savePreference(() => theme.setTheme('Dark'))" /> {{ t('shell.myProfile.themeDark') }}</label>
        </p>
        <p>
          <label><input type="radio" name="theme" value="HighVisibility" :checked="theme.current === 'HighVisibility'" @change="savePreference(() => theme.setTheme('HighVisibility'))" /> {{ t('shell.myProfile.themeHighVisibility') }}</label>
        </p>
      </fieldset>

      <h2>{{ t('shell.myProfile.language') }}</h2>
      <fieldset>
        <legend>{{ t('shell.myProfile.languageLegend') }}</legend>
        <p>
          <label><input type="radio" name="language" value="" :checked="!locale.explicit" @change="savePreference(() => locale.useBrowserDefault())" /> {{ t('shell.myProfile.languageBrowserDefault') }} (<span :lang="locale.current">{{ languageName(locale.current) }}</span>)</label>
        </p>
        <p v-for="code in locale.available" :key="code">
          <label><input type="radio" name="language" :value="code" :checked="locale.explicit && locale.current === code" @change="savePreference(() => locale.setLanguage(code))" /> <span :lang="code">{{ languageName(code) }}</span></label>
        </p>
      </fieldset>

      <h2>{{ t('shell.myProfile.pageSizeHeading') }}</h2>
      <p>
        <label class="field-label"><span class="field-label-text">{{ t('shell.myProfile.pageSizeLabel') }}</span>
          <select :value="pageSize.current" @change="savePreference(() => pageSize.setPageSize($event.target.value))">
            <option v-for="size in PAGE_SIZE_OPTIONS" :key="size" :value="size">{{ size }}</option>
          </select>
        </label>
      </p>

      <p v-if="prefStatus?.ok" role="status">{{ t('common.saved') }}</p>
      <p v-else-if="prefStatus" role="alert">{{ t('shell.myProfile.prefNotSaved') }}</p>
    </template>
  </div>
</template>
