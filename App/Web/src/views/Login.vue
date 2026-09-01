<script setup>
// D-41: defaults straight to the configured default provider (last-used per
// D-02, or an admin-configured system default) rather than a full choice
// screen; a small link exposes the other enabled providers, including
// break-glass. Actual provider redirect logic depends on the identity
// providers admin screen existing and GET /api/identity-providers being
// built -- placeholder until then.
</script>

<template>
  <div>
    <h1>Sign in</h1>
    <p>Provider redirect logic not yet implemented (D-41).</p>
  </div>
</template>
