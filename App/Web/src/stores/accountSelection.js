import { useListSelectionStore } from './listSelection'

// D-182: the Account Progress bulk-action selection -- the shared list
// selection store (D-190), under the 'accounts' list.
export const useAccountSelectionStore = () => useListSelectionStore('accounts')
