<script setup>
// UI review W1-2: an address that matches no page. (A page the user may not open is AccessDenied.vue.)
import { useI18n } from 'vue-i18n'

const { t } = useI18n()
</script>

<template>
  <div>
    <h1>{{ t('shell.notFound.title') }}</h1>
    <p>{{ t('shell.notFound.message') }}</p>
    <p><router-link :to="{ name: 'dashboard' }">{{ t('shell.notFound.home') }}</router-link></p>
  </div>
</template>
