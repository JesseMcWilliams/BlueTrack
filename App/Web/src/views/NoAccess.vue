<script setup>
// UI review step 4b (W1-1): where the router sends a signed-in user whose account holds no permissions (see
// router/routePermissions.js). It says who they are signed in as, what to ask for and of whom, and lets them re-check
// without signing out.
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { useRightsStore } from '../stores/rights'
import { useSignOut } from '../composables/useSignOut'

const { t } = useI18n()
const router = useRouter()
const rights = useRightsStore()
const signOutNow = useSignOut()

const stillNone = ref(false)
const signOutError = ref(null)

async function checkAgain() {
  stillNone.value = false
  await rights.reload()
  if (rights.error) return
  if (rights.permissionNames.length > 0) {
    await router.push({ name: 'dashboard' })
  } else {
    stillNone.value = true
  }
}

async function signOut() {
  signOutError.value = null
  try {
    await signOutNow()
  } catch (err) {
    signOutError.value = err.message
  }
}
</script>

<template>
  <div>
    <h1>{{ t('shell.noAccess.title') }}</h1>
    <p>{{ t('shell.noAccess.signedInAs', { name: rights.displayName || '' }) }}</p>
    <h2>{{ t('shell.noAccess.whatToDo') }}</h2>
    <ol>
      <li>{{ t('shell.noAccess.step1') }}</li>
      <li>{{ t('shell.noAccess.step2') }}</li>
    </ol>
    <p>
      <button type="button" :disabled="rights.loading" @click="checkAgain">{{ t('shell.noAccess.checkAgain') }}</button>
      <button type="button" @click="signOut">{{ t('shell.noAccess.signOut') }}</button>
      <router-link :to="{ name: 'my-profile' }">{{ t('shell.noAccess.profile') }}</router-link>
    </p>
    <p v-if="stillNone" role="status">{{ t('shell.noAccess.stillNone') }}</p>
    <p v-if="rights.error" role="alert">{{ rights.error }}</p>
    <p v-if="signOutError" role="alert">{{ signOutError }}</p>
  </div>
</template>
