<script setup>
// D-314: what someone building a Power BI report on BlueTrack's data needs: the server and database, the views and
// tables meant for it (with their columns as the database has them now), how they relate, and a script that gives an
// account read access to just those objects.
import { ref, computed, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const access = ref(null)
const error = ref(null)
const loading = ref(true)
const reader = ref('')

const facts = computed(() => access.value?.objects.filter(o => o.kind === 'Fact') ?? [])
const dimensions = computed(() => access.value?.objects.filter(o => o.kind === 'Dimension') ?? [])

// A name in brackets, with any ] doubled, as T-SQL quotes it; a placeholder until one is typed.
const readerName = computed(() => {
  const name = reader.value.trim()
  return name ? `[${name.replaceAll(']', ']]')}]` : '[DOMAIN\\PowerBI-Readers]'
})
// D-321: with the BlueTrack_PowerBI_Reader role in place, adding a member is enough; without it, the grants themselves.
const grantScript = computed(() => {
  if (!access.value) return ''
  const lines = [`USE [${access.value.databaseName.replaceAll(']', ']]')}];`]
  if (access.value.readerRoleExists) {
    lines.push(`CREATE USER ${readerName.value} FOR LOGIN ${readerName.value};   -- if it isn't a user of this database yet`)
    lines.push(`ALTER ROLE BlueTrack_PowerBI_Reader ADD MEMBER ${readerName.value};`)
  } else {
    for (const o of access.value.objects) lines.push(`GRANT SELECT ON [${o.schema}].[${o.name}] TO ${readerName.value};`)
  }
  return lines.join('\n')
})

onMounted(async () => {
  try {
    const response = await fetch('/api/admin/power-bi')
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    access.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
})
</script>

<template>
  <div>
    <h2>{{ t('admin.powerBi.title') }}</h2>
    <p>{{ t('admin.powerBi.intro') }}</p>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="error" role="alert">{{ t('admin.powerBi.loadFailed', { error }) }}</p>
    <template v-else-if="access">
      <h3>{{ t('admin.powerBi.connectTitle') }}</h3>
      <dl class="power-bi-connect">
        <dt>{{ t('admin.powerBi.server') }}</dt><dd><code>{{ access.serverName }}</code></dd>
        <dt>{{ t('admin.powerBi.database') }}</dt><dd><code>{{ access.databaseName }}</code></dd>
      </dl>
      <p>{{ t('admin.powerBi.connectHint') }}</p>
      <p>{{ t('admin.powerBi.refreshHint') }}</p>

      <h3>{{ t('admin.powerBi.objectsTitle') }}</h3>
      <template v-for="group in [{ titleKey: 'admin.powerBi.facts', items: facts }, { titleKey: 'admin.powerBi.dimensions', items: dimensions }]" :key="group.titleKey">
        <h4>{{ t(group.titleKey) }}</h4>
        <table class="power-bi-objects">
          <thead><tr><th>{{ t('admin.powerBi.object') }}</th><th>{{ t('admin.powerBi.description') }}</th><th>{{ t('admin.powerBi.columns') }}</th></tr></thead>
          <tbody>
            <tr v-for="o in group.items" :key="o.name">
              <td><code>{{ o.schema }}.{{ o.name }}</code></td>
              <td>{{ o.description }}</td>
              <td class="power-bi-columns">{{ o.columns.join(', ') }}</td>
            </tr>
          </tbody>
        </table>
      </template>

      <h3>{{ t('admin.powerBi.relationshipsTitle') }}</h3>
      <p>{{ t('admin.powerBi.relationshipsHint') }}</p>
      <table>
        <thead><tr><th>{{ t('admin.powerBi.from') }}</th><th>{{ t('admin.powerBi.to') }}</th></tr></thead>
        <tbody>
          <tr v-for="r in access.relationships" :key="`${r.fromObject}.${r.fromColumn}`">
            <td><code>{{ r.fromObject }}.{{ r.fromColumn }}</code></td>
            <td><code>{{ r.toObject }}.{{ r.toColumn }}</code></td>
          </tr>
        </tbody>
      </table>

      <h3>{{ t('admin.powerBi.accessTitle') }}</h3>
      <p>{{ access.readerRoleExists ? t('admin.powerBi.roleExists') : t('admin.powerBi.roleMissing') }}</p>
      <p>{{ t('admin.powerBi.accessHint') }}</p>
      <p>
        <label class="field-label"><span class="field-label-text">{{ t('admin.powerBi.reader') }}</span>
          <input v-model="reader" :placeholder="t('admin.powerBi.readerPlaceholder')" size="40" /></label>
      </p>
      <pre class="power-bi-script">{{ grantScript }}</pre>
    </template>
  </div>
</template>

<style scoped>
.power-bi-connect {
  display: grid;
  grid-template-columns: max-content 1fr;
  gap: var(--space-1) var(--space-3);
}
.power-bi-connect dt { font-weight: bold; }
.power-bi-connect dd { margin: 0; }
.power-bi-columns { font-size: 0.875em; }
.power-bi-script {
  border: 1px solid var(--color-border);
  padding: var(--space-2);
  overflow-x: auto;
}
</style>
