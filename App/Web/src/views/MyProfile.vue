<script setup>
// Self-service "Reload My Rights" (D-14) -- re-resolves group membership
// live via POST /api/me/reload-rights and updates the shared rights store
// every permission-aware control on the page reads from. Also the theme
// picker (Design_Accessibility-And-Theming.md, D-93) -- confirmed location
// for this per-user preference, alongside the other self-service settings.
// D-227: and the language picker, which lists only the registered locales
// (src/i18n/index.js SUPPORTED_LOCALES), each by its own name.
import { onMounted } from 'vue'
import { useRightsStore } from '../stores/rights'
import { useThemeStore } from '../stores/theme'
import { useLocaleStore, languageName } from '../stores/locale'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const rights = useRightsStore()
const theme = useThemeStore()
const locale = useLocaleStore()

onMounted(() => rights.ensureLoaded())
</script>

<template>
  <div>
    <h1>{{ t('shell.myProfile.title') }}</h1>
    <p v-if="rights.loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="rights.error" role="alert">{{ rights.error }}</p>
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
      <button :disabled="rights.loading" @click="rights.reload">{{ t('shell.myProfile.reloadRights') }}</button>
      <p><small>{{ t('shell.myProfile.reloadRightsHint') }}</small></p>

      <h2>{{ t('shell.myProfile.theme') }}</h2>
      <fieldset>
        <legend>{{ t('shell.myProfile.themeLegend') }}</legend>
        <p>
          <label><input type="radio" name="theme" value="Light" :checked="theme.current === 'Light'" @change="theme.setTheme('Light')" /> {{ t('shell.myProfile.themeLight') }}</label>
        </p>
        <p>
          <label><input type="radio" name="theme" value="Dark" :checked="theme.current === 'Dark'" @change="theme.setTheme('Dark')" /> {{ t('shell.myProfile.themeDark') }}</label>
        </p>
        <p>
          <label><input type="radio" name="theme" value="HighVisibility" :checked="theme.current === 'HighVisibility'" @change="theme.setTheme('HighVisibility')" /> {{ t('shell.myProfile.themeHighVisibility') }}</label>
        </p>
      </fieldset>

      <h2>{{ t('shell.myProfile.language') }}</h2>
      <fieldset>
        <legend>{{ t('shell.myProfile.languageLegend') }}</legend>
        <p>
          <label><input type="radio" name="language" value="" :checked="!locale.explicit" @change="locale.useBrowserDefault()" /> {{ t('shell.myProfile.languageBrowserDefault') }} (<span :lang="locale.current">{{ languageName(locale.current) }}</span>)</label>
        </p>
        <p v-for="code in locale.available" :key="code">
          <label><input type="radio" name="language" :value="code" :checked="locale.explicit && locale.current === code" @change="locale.setLanguage(code)" /> <span :lang="code">{{ languageName(code) }}</span></label>
        </p>
      </fieldset>
    </template>
  </div>
</template>
