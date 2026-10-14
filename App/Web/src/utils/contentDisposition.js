// D-220: the file name a download response gives in its Content-Disposition
// header. Prefers RFC 5987's filename*=UTF-8''..., then filename="..."; null
// when there's none.
export function fileNameFrom(contentDisposition) {
  if (!contentDisposition) return null
  const extended = /filename\*\s*=\s*UTF-8''([^;]+)/i.exec(contentDisposition)
  if (extended) {
    try {
      return decodeURIComponent(extended[1].trim())
    } catch {
      // A malformed escape: fall through to the plain form.
    }
  }
  const plain = /filename\s*=\s*"?([^";]+)"?/i.exec(contentDisposition)
  return plain ? plain[1].trim() : null
}
