<script setup>
// UI review W1-2: this page is for a page the user may not open. A missing address is NotFound.vue. Both offer a way on.
import { useI18n } from 'vue-i18n'

const { t } = useI18n()
</script>

<template>
  <div>
    <h1>{{ t('shell.accessDenied.title') }}</h1>
    <p>{{ t('shell.accessDenied.message') }}</p>
    <p>
      <router-link :to="{ name: 'dashboard' }">{{ t('shell.accessDenied.home') }}</router-link>
      |
      <router-link :to="{ name: 'my-profile' }">{{ t('shell.accessDenied.profile') }}</router-link>
    </p>
  </div>
</template>
