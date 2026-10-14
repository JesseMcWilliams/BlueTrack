<script setup>
import { useI18n } from 'vue-i18n'

const { t } = useI18n()
</script>

<template>
  <div>
    <h1>{{ t('shell.accessDenied.title') }}</h1>
    <p>{{ t('shell.accessDenied.message') }}</p>
  </div>
</template>
