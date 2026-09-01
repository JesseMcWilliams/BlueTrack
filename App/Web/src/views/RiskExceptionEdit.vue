<script setup>
// Create/edit, account- or application-scoped (D-18/D-31). Not yet implemented.
defineProps({ exceptionKey: { type: [String, Number], required: false, default: null } })
</script>

<template>
  <div>
    <h1>{{ exceptionKey ? `Exception — ${exceptionKey}` : 'New Exception' }}</h1>
    <p>Create/edit form not yet implemented.</p>
  </div>
</template>
