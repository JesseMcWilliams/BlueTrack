<script setup>
// D-206: the top nav's user menu, replacing the plain "My Profile" link.
// The button shows who is signed in; it opens a short list with
// Profile & Preferences (the old My Profile page) and Sign out. A disclosure
// button (aria-expanded) rather than an ARIA menu: a list of a link and a
// button needs no menu keyboard model, and screen readers handle it plainly.
// Closes on Escape (focus back on the button), on a click outside, and when
// the route changes. D-213: About opens a small dialog with the version.
import { ref, computed, watch, onMounted, onBeforeUnmount } from 'vue'
import { useRoute } from 'vue-router'
import { useRightsStore } from '../stores/rights'
import { useSignOut } from '../composables/useSignOut'
import AboutDialog from './AboutDialog.vue'

const rights = useRightsStore()
const route = useRoute()
const signOutNow = useSignOut()
const open = ref(false)
const signingOut = ref(false)
const error = ref(null)
const root = ref(null)
const button = ref(null)
const aboutOpen = ref(false)

function openAbout() {
  close()
  aboutOpen.value = true
}

function closeAbout() {
  aboutOpen.value = false
  button.value?.focus()
}

const label = computed(() => rights.displayName || 'Account')

function close({ focusButton = false } = {}) {
  open.value = false
  if (focusButton) button.value?.focus()
}

function onDocumentClick(event) {
  if (open.value && root.value && !root.value.contains(event.target)) close()
}

function onKeydown(event) {
  if (event.key === 'Escape' && open.value) close({ focusButton: true })
}

watch(() => route.fullPath, () => close())
onMounted(() => document.addEventListener('click', onDocumentClick))
onBeforeUnmount(() => document.removeEventListener('click', onDocumentClick))

async function signOut() {
  signingOut.value = true
  error.value = null
  try {
    close()
    await signOutNow()
  } catch (err) {
    error.value = err.message
  } finally {
    signingOut.value = false
  }
}
</script>

<template>
  <div ref="root" class="user-menu" @keydown="onKeydown">
    <button
      ref="button"
      type="button"
      class="user-menu__button"
      aria-controls="user-menu-list"
      :aria-expanded="open ? 'true' : 'false'"
      @click="open = !open"
    >
      {{ label }} <span aria-hidden="true">▾</span>
    </button>
    <ul v-show="open" id="user-menu-list" class="user-menu__list">
      <li><router-link :to="{ name: 'my-profile' }">Profile &amp; Preferences</router-link></li>
      <li><button type="button" @click="openAbout">About</button></li>
      <li><button type="button" :disabled="signingOut" @click="signOut">{{ signingOut ? 'Signing out...' : 'Sign out' }}</button></li>
    </ul>
    <p v-if="error" role="alert" class="user-menu__error">{{ error }}</p>
    <AboutDialog v-if="aboutOpen" @close="closeAbout" />
  </div>
</template>

<style scoped>
.user-menu {
  position: relative;
  margin-left: auto;
}
.user-menu__list {
  position: absolute;
  right: 0;
  z-index: 10;
  min-width: 12rem;
  margin: var(--space-2) 0 0;
  padding: var(--space-2) 0;
  list-style: none;
  background: var(--color-bg);
  border: 1px solid var(--color-border);
}
.user-menu__list li > a,
.user-menu__list li > button {
  display: block;
  width: 100%;
  padding: var(--space-2) var(--space-4);
  text-align: left;
}
.user-menu__error {
  position: absolute;
  right: 0;
  margin: 0;
  white-space: nowrap;
}
</style>
