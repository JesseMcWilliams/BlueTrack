<script setup>
// D-207: the import mapping profile for one Bulk Actions upload. None (the
// default) means the CSV uses the template's own column names; before this,
// an upload silently used the oldest active profile for its feed type.
// Shown only when the feed type has an active profile.
import { ref, onMounted } from 'vue'

const props = defineProps({
  feedType: { type: String, required: true },
  // 'target' or 'access-group': which permission-gated list the profiles come from.
  area: { type: String, required: true }
})
const model = defineModel({ type: Number, default: null })

const profiles = ref([])

onMounted(async () => {
  try {
    const response = await fetch(`/api/admin/risk-scoring/import/${props.area}-mapping-profiles?feedType=${encodeURIComponent(props.feedType)}`)
    if (response.ok) profiles.value = await response.json()
  } catch {
    // No list: the upload just uses the template's column names.
  }
})
</script>

<template>
  <label v-if="profiles.length > 0" class="field-label">
    <span class="field-label-text">Mapping profile:</span>
    <select v-model="model">
      <option :value="null">None (the template's own column names)</option>
      <option v-for="p in profiles" :key="p.importMappingProfileKey" :value="p.importMappingProfileKey">{{ p.profileName }}</option>
    </select>
  </label>
</template>
