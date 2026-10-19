import { watch, nextTick, inject } from 'vue'
import { routeLocationKey, routerKey } from 'vue-router'
import { sameQuery } from '../utils/listQuery'

/**
 * UI review W7-2: keeps a page's filters, sort and page in its address, so a report can be shared, repeated and
 * reloaded.
 *
 * - `apply(query)` is called once now with the address's query, and again when the address changes under the page
 *   (Back, Forward, a link to the same page): set the page's state from it. A value the page doesn't have should be
 *   ignored there (see utils/listQuery.js).
 * - `current()` is the query the page's state says (empty values and defaults left out: `compactQuery`).
 * - `sources` is what to watch for a change: a ref, a getter, or an array of them. A change replaces the address
 *   (not a new history entry, so Back leaves the page instead of stepping through every keystroke).
 * - `onApplied()` runs after an address change was applied, for a page that loads by hand rather than from a watcher.
 * - `remember(query)` (optional) is told the page's current query at the start and after every change, for a page that
 *   hands it to the listState store so the way back to the list keeps the view.
 * - Returns `{ isApplying }`: true while an address change is being applied, so a page's own "a filter or sort
 *   changed, go to page 1" watcher can skip the changes the address made.
 *
 * A page that isn't in a router (a test mounting it alone) just never touches an address.
 */
export function useAddressState({ sources, current, apply, onApplied, remember }) {
  // inject with a default, so a page mounted outside a router (a test) is quiet rather than warning
  const route = inject(routeLocationKey, null)
  const router = inject(routerKey, null)
  const routeName = route?.name
  let applying = false

  if (route) apply(route.query)
  remember?.(current())

  function write() {
    if (applying || !route || !router || route.name !== routeName) return
    const query = current()
    remember?.(query)
    if (!sameQuery(query, route.query)) router.replace({ query })
  }
  watch(sources, write, { flush: 'post', deep: true })

  if (route) {
    watch(() => route.query, async (query) => {
      if (route.name !== routeName || sameQuery(query, current())) return
      applying = true
      apply(query)
      await nextTick()
      applying = false
      remember?.(current())
      onApplied?.()
    })
  }
  return { isApplying: () => applying }
}
