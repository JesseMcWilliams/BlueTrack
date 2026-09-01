<script setup>
// Field-metadata-driven edit form (Design_Interface_Extensibility.md) with
// pessimistic locking (D-50) and the two validation rules from D-51. None
// of that is implemented yet -- placeholder.
defineProps({ accountKey: { type: [String, Number], required: true } })
</script>

<template>
  <div>
    <h1>Account Progress — {{ accountKey }}</h1>
    <p>Field-metadata-driven edit form not yet implemented (locking D-50, validation D-51).</p>
  </div>
</template>
