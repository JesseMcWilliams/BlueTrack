// A list page reloads on every filter, search, sort and page change, and the replies can arrive out of order: a
// slow reply to an earlier request would overwrite the newer one's rows (found in E2E, 2026-10-18: choosing
// "Deleted accounts: Only" straight after typing a search sometimes showed the search's rows instead). A page calls
// begin() when it starts a request and drops the reply, error and loading flag of any request that isn't the
// newest (isCurrent).
export function useLatestRequest() {
  let newest = 0
  return {
    begin: () => ++newest,
    isCurrent: request => request === newest
  }
}
