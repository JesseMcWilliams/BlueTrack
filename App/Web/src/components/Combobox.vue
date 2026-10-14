<script setup>
// D-225: the app's one reusable typeahead -- the WAI-ARIA APG combobox
// pattern, "list autocomplete with manual selection"
// (https://www.w3.org/WAI/ARIA/apg/patterns/combobox/). A text input
// (role=combobox) owns a listbox popup of results the parent's `search`
// function returns. Typing waits `debounceMs` after the last keystroke and
// searches only from `minLength` characters; a slower, older response never
// replaces a newer one. Down/Up move the active option (aria-activedescendant
// -- focus stays in the input), Enter picks it, Escape closes the list (or,
// when already closed, clears the text). Alt+Down opens the last results.
// The picked item is the v-model; typing again clears it. The number of
// results is announced in a polite live region.
import { ref, computed, watch, onBeforeUnmount, useId } from 'vue'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const props = defineProps({
  // Accessible name of the input (use instead of a visible <label for>).
  label: { type: String, default: null },
  // async (text) => items[]; may throw -- the message is shown.
  search: { type: Function, required: true },
  itemKey: { type: Function, required: true },
  // The text shown in the input once an item is picked.
  itemLabel: { type: Function, required: true },
  minLength: { type: Number, default: 2 },
  debounceMs: { type: Number, default: 250 },
  placeholder: { type: String, default: '' },
  inputId: { type: String, default: null }
})
const model = defineModel({ type: Object, default: null })
const emit = defineEmits(['select'])

const uid = useId()
const id = computed(() => props.inputId ?? `combobox-${uid}`)
const listboxId = computed(() => `${id.value}-listbox`)
const hintId = computed(() => `${id.value}-hint`)

const text = ref(model.value ? props.itemLabel(model.value) : '')
const items = ref([])
const open = ref(false)
const activeIndex = ref(-1)
const status = ref('')
const loading = ref(false)
let timer = null
let requestSeq = 0

// A value the parent sets shows its label in the input.
watch(model, value => {
  if (value) text.value = props.itemLabel(value)
})

const activeId = computed(() => (open.value && activeIndex.value >= 0 ? optionId(activeIndex.value) : undefined))

function optionId(index) {
  return `${id.value}-option-${index}`
}

function close() {
  open.value = false
  activeIndex.value = -1
}

async function runSearch(query) {
  const seq = ++requestSeq
  loading.value = true
  try {
    const results = await props.search(query)
    if (seq !== requestSeq) return
    items.value = Array.isArray(results) ? results : []
    activeIndex.value = -1
    open.value = items.value.length > 0
    status.value = items.value.length === 0
      ? t('shell.combobox.noMatches')
      : t('shell.combobox.matches', { count: items.value.length }, items.value.length)
  } catch (err) {
    if (seq !== requestSeq) return
    items.value = []
    close()
    status.value = err?.message ?? t('shell.combobox.searchFailed')
  } finally {
    if (seq === requestSeq) loading.value = false
  }
}

function onInput(event) {
  text.value = event.target.value
  if (model.value) model.value = null
  clearTimeout(timer)
  const query = text.value.trim()
  if (query.length < props.minLength) {
    requestSeq++ // drop any search still in flight
    loading.value = false
    items.value = []
    close()
    status.value = query.length === 0 ? '' : t('shell.combobox.typeAtLeast', { count: props.minLength })
    return
  }
  timer = setTimeout(() => runSearch(query), props.debounceMs)
}

function pick(index) {
  const item = items.value[index]
  if (!item) return
  model.value = item
  text.value = props.itemLabel(item)
  close()
  status.value = t('shell.combobox.selected', { label: props.itemLabel(item) })
  emit('select', item)
}

function move(step) {
  if (!open.value) {
    if (items.value.length === 0) return
    open.value = true
  }
  const count = items.value.length
  activeIndex.value = activeIndex.value < 0
    ? (step > 0 ? 0 : count - 1)
    : (activeIndex.value + step + count) % count
  document.getElementById(optionId(activeIndex.value))?.scrollIntoView?.({ block: 'nearest' })
}

function onKeydown(event) {
  switch (event.key) {
    case 'ArrowDown':
      event.preventDefault()
      if (event.altKey) {
        if (items.value.length > 0) open.value = true
      } else {
        move(1)
      }
      break
    case 'ArrowUp':
      event.preventDefault()
      move(-1)
      break
    case 'Enter':
      if (open.value && activeIndex.value >= 0) {
        event.preventDefault() // pick, don't submit the surrounding form
        pick(activeIndex.value)
      }
      break
    case 'Escape':
      if (open.value) {
        event.preventDefault()
        close()
      } else if (text.value) {
        event.preventDefault()
        text.value = ''
        model.value = null
        items.value = []
        status.value = ''
      }
      break
  }
}

function onBlur() {
  close()
}

// Picks on mousedown's click without moving focus out of the input first.
function onOptionMousedown(event) {
  event.preventDefault()
}

function clear() {
  text.value = ''
  model.value = null
  items.value = []
  close()
}
defineExpose({ clear })

onBeforeUnmount(() => clearTimeout(timer))
</script>

<template>
  <div class="combobox">
    <input
      :id="id"
      type="text"
      role="combobox"
      autocomplete="off"
      aria-autocomplete="list"
      :aria-label="label ?? undefined"
      :aria-expanded="open ? 'true' : 'false'"
      :aria-controls="listboxId"
      :aria-activedescendant="activeId"
      :aria-describedby="hintId"
      :aria-busy="loading ? 'true' : 'false'"
      :placeholder="placeholder"
      :value="text"
      @input="onInput"
      @keydown="onKeydown"
      @blur="onBlur"
    />
    <ul v-show="open" :id="listboxId" role="listbox" :aria-label="label ?? undefined" class="combobox-listbox">
      <li
        v-for="(item, index) in items"
        :id="optionId(index)"
        :key="itemKey(item)"
        role="option"
        :aria-selected="index === activeIndex ? 'true' : 'false'"
        class="combobox-option"
        @mousedown="onOptionMousedown"
        @click="pick(index)"
        @mousemove="activeIndex = index"
      >
        <slot name="option" :item="item">{{ itemLabel(item) }}</slot>
      </li>
    </ul>
    <span :id="hintId" class="visually-hidden">{{ t('shell.combobox.hint', { count: minLength }) }}</span>
    <span class="visually-hidden" role="status" aria-live="polite">{{ status }}</span>
  </div>
</template>

<style scoped>
.combobox {
  position: relative;
  display: inline-block;
  min-width: 16em;
}
.combobox > input {
  width: 100%;
}
.combobox-listbox {
  position: absolute;
  z-index: 10;
  left: 0;
  right: 0;
  min-width: 24em;
  max-height: 20em;
  overflow-y: auto;
  margin: var(--space-1) 0 0;
  padding: 0;
  list-style: none;
  background: var(--color-bg);
  color: var(--color-text);
  border: 1px solid var(--color-border);
  border-radius: 0.25rem;
}
.combobox-option {
  padding: var(--space-2) var(--space-3);
  cursor: pointer;
  border-bottom: 1px solid var(--color-border);
}
.combobox-option:last-child {
  border-bottom: 0;
}
/* The active option: a background tint plus an outline, so it doesn't
   depend on color alone and still shows in Forced Colors Mode. */
.combobox-option[aria-selected='true'] {
  background: var(--color-row-hover);
  outline: 2px solid var(--color-focus-ring);
  outline-offset: -2px;
}
</style>
