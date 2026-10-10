<script setup>
// D-190: "tick Change for each field to set" -- the same idea as Account
// Progress's Bulk Edit page (D-182), as a small panel for the other lists.
// Emits apply({ fields: [...names], <name>: value, ... }) with only the
// ticked fields; the API leaves every other field as it is on each item.
import { ref, computed } from 'vue'

const props = defineProps({
  /** [{ field, label, type: 'select' | 'number' | 'text', options?: [{ value, label }], required?: bool }] */
  fields: { type: Array, required: true },
  count: { type: Number, required: true },
  saving: { type: Boolean, default: false }
})
const emit = defineEmits(['apply', 'cancel'])

const change = ref(Object.fromEntries(props.fields.map(f => [f.field, false])))
const values = ref(Object.fromEntries(props.fields.map(f => [f.field, null])))
const chosen = computed(() => props.fields.filter(f => change.value[f.field]))

function apply() {
  const payload = { fields: chosen.value.map(f => f.field) }
  for (const f of chosen.value) {
    const value = values.value[f.field]
    payload[f.field.charAt(0).toLowerCase() + f.field.slice(1)] = value === '' ? null : value
  }
  emit('apply', payload)
}
</script>

<template>
  <form class="bulk-panel" @submit.prevent="apply">
    <p>Tick <em>Change</em> for each field to set on the {{ count }} selected; other fields stay as they are.</p>
    <table>
      <thead><tr><th>Change</th><th>Field</th><th>New value</th></tr></thead>
      <tbody>
        <tr v-for="f in fields" :key="f.field">
          <td><input v-model="change[f.field]" type="checkbox" :aria-label="`Change ${f.label}`" /></td>
          <td>{{ f.label }}</td>
          <td>
            <select v-if="f.type === 'select'" v-model="values[f.field]" :disabled="!change[f.field]" :aria-label="f.label" :required="change[f.field] && f.required">
              <option v-if="!f.required" :value="null">(blank)</option>
              <option v-for="o in f.options" :key="o.value" :value="o.value">{{ o.label }}</option>
            </select>
            <input v-else-if="f.type === 'number'" v-model.number="values[f.field]" type="number" min="0" :disabled="!change[f.field]" :aria-label="f.label" :required="change[f.field]" />
            <input v-else v-model="values[f.field]" :disabled="!change[f.field]" :aria-label="f.label" placeholder="blank clears it" size="40" />
          </td>
        </tr>
      </tbody>
    </table>
    <button type="submit" class="btn-primary" :disabled="saving || chosen.length === 0">Apply to {{ count }}</button>
    <button type="button" @click="emit('cancel')">Cancel</button>
  </form>
</template>

<style scoped>
.bulk-panel {
  margin: 0.5rem 0 1rem;
}
</style>
