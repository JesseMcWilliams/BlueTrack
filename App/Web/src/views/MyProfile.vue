<script setup>
// Self-service "Reload My Rights" (D-14) -- re-resolves group membership
// live via POST /api/me/reload-rights and updates the shared rights store
// every permission-aware control on the page reads from. Also the theme
// picker (Design_Accessibility_And_Theming.md, D-93) -- confirmed location
// for this per-user preference, alongside the other self-service settings.
import { onMounted } from 'vue'
import { useRightsStore } from '../stores/rights'
import { useThemeStore } from '../stores/theme'

const rights = useRightsStore()
const theme = useThemeStore()

onMounted(() => rights.ensureLoaded())
</script>

<template>
  <div>
    <h1>My Profile</h1>
    <p v-if="rights.loading" role="status">Loading...</p>
    <p v-else-if="rights.error" role="alert">{{ rights.error }}</p>
    <template v-else>
      <dl>
        <dt>Name</dt>
        <dd>{{ rights.displayName }}</dd>
        <dt>Role(s)</dt>
        <dd>{{ rights.roleNames.join(', ') || '(none mapped)' }}</dd>
        <dt>Permission(s)</dt>
        <dd>{{ rights.permissionNames.join(', ') || '(none)' }}</dd>
      </dl>
      <button :disabled="rights.loading" @click="rights.reload">Reload My Rights</button>
      <p><small>Re-checks your current group membership immediately, without waiting for anything to expire -- useful right after you know you've been added to a new group.</small></p>

      <h2>Theme</h2>
      <fieldset>
        <legend>Choose a display theme</legend>
        <p>
          <label><input type="radio" name="theme" value="Light" :checked="theme.current === 'Light'" @change="theme.setTheme('Light')" /> Light</label>
        </p>
        <p>
          <label><input type="radio" name="theme" value="Dark" :checked="theme.current === 'Dark'" @change="theme.setTheme('Dark')" /> Dark</label>
        </p>
        <p>
          <label><input type="radio" name="theme" value="HighVisibility" :checked="theme.current === 'HighVisibility'" @change="theme.setTheme('HighVisibility')" /> High Visibility</label>
        </p>
      </fieldset>
    </template>
  </div>
</template>
