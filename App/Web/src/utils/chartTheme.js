// D-221: chart colours come from the theme tokens in assets/themes.css, read
// at runtime (Chart.js draws on a canvas, which CSS can't style), and are
// read again whenever <html data-theme> changes (stores/theme.js), so a
// chart redraws in the new theme without a reload.
import { ref, onMounted, onBeforeUnmount } from 'vue'

function token(styles, name, fallback) {
  const value = styles.getPropertyValue(name).trim()
  return value || fallback
}

export function readChartTheme() {
  const styles = getComputedStyle(document.documentElement)
  return {
    text: token(styles, '--color-text', '#000000'),
    background: token(styles, '--color-bg', '#ffffff'),
    grid: token(styles, '--color-chart-grid', '#e1e0d9'),
    line: token(styles, '--color-chart-line', '#2a78d6'),
    stages: [1, 2, 3, 4, 5].map(n => token(styles, `--color-chart-stage-${n}`, '#2a78d6')),
    risks: [1, 2, 3, 4].map(n => token(styles, `--color-chart-risk-${n}`, '#2a78d6')),
    riskNone: token(styles, '--color-chart-risk-none', '#898781')
  }
}

// A ref holding the current chart theme, refreshed on every theme change.
export function useChartTheme() {
  const theme = ref(readChartTheme())
  let observer = null
  onMounted(() => {
    theme.value = readChartTheme()
    observer = new MutationObserver(() => { theme.value = readChartTheme() })
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] })
  })
  onBeforeUnmount(() => observer?.disconnect())
  return theme
}
