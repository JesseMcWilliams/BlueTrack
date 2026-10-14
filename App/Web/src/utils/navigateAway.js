// D-217: a full browser navigation (leaving the SPA), in its own module so tests can replace it.
export function navigateAway(url) {
  window.location.assign(url)
}
