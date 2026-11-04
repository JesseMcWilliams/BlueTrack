// D-330: whether a hub sidebar entry (Admin, Reports) is the current page. A section's own pages (a provider's edit
// page, Data Sources' CyberArk sources) are sibling routes, not children, so the router's active class misses them;
// they share the section's path, so the path decides.
import { useRoute, useRouter } from 'vue-router'

export function useSubnavCurrent() {
  const route = useRoute()
  const router = useRouter()
  return (name) => {
    const path = router.resolve({ name }).path
    return route.path === path || route.path.startsWith(`${path}/`)
  }
}
