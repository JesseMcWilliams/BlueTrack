<script setup>
// Self-service "Reload My Rights" (D-14) -- re-resolves group membership
// live via POST /api/me/reload-rights and updates the shared rights store
// every permission-aware control on the page reads from.
import { onMounted } from 'vue'
import { useRightsStore } from '../stores/rights'

const rights = useRightsStore()

onMounted(() => rights.ensureLoaded())
</script>

<template>
  <div>
    <h1>My Profile</h1>
    <p v-if="rights.loading">Loading...</p>
    <p v-else-if="rights.error">{{ rights.error }}</p>
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
    </template>
  </div>
</template>
