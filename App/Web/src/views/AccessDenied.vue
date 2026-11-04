<script setup>
// UI review W1-2: this page is for a page the user may not open. A missing address is NotFound.vue. Both offer a way on.
// D-307: the way on is Home, which everyone with a role may open (D-304 offered the Dashboard, which not all may).
import { useI18n } from 'vue-i18n'

const { t } = useI18n()
</script>

<template>
  <div>
    <h1>{{ t('shell.accessDenied.title') }}</h1>
    <p>{{ t('shell.accessDenied.message') }}</p>
    <p>
      <router-link :to="{ name: 'home' }">{{ t('shell.accessDenied.home') }}</router-link>
      |
      <router-link :to="{ name: 'my-profile' }">{{ t('shell.accessDenied.profile') }}</router-link>
    </p>
  </div>
</template>
