import { inflateRawSync } from 'node:zlib'

// D-220: just enough of an .xlsx reader to check an export's header row,
// without adding a spreadsheet library to the E2E project. An .xlsx is a zip
// of XML parts: read the zip's central directory, inflate the parts needed,
// and pull row 1's cell text out of the first sheet (shared or inline strings).

function zipEntries(buffer) {
  // End of central directory record: signature 0x06054b50, within the last 64 KB.
  let eocd = -1
  for (let i = buffer.length - 22; i >= Math.max(0, buffer.length - 65557); i--) {
    if (buffer.readUInt32LE(i) === 0x06054b50) { eocd = i; break }
  }
  if (eocd < 0) throw new Error('Not a zip file')
  const count = buffer.readUInt16LE(eocd + 10)
  let offset = buffer.readUInt32LE(eocd + 16)
  const entries = new Map()
  for (let n = 0; n < count; n++) {
    const method = buffer.readUInt16LE(offset + 10)
    const compressedSize = buffer.readUInt32LE(offset + 20)
    const nameLength = buffer.readUInt16LE(offset + 28)
    const extraLength = buffer.readUInt16LE(offset + 30)
    const commentLength = buffer.readUInt16LE(offset + 32)
    const localHeader = buffer.readUInt32LE(offset + 42)
    const name = buffer.toString('utf8', offset + 46, offset + 46 + nameLength)
    entries.set(name, { method, compressedSize, localHeader })
    offset += 46 + nameLength + extraLength + commentLength
  }
  return {
    read(name) {
      const entry = entries.get(name)
      if (!entry) return null
      const start = entry.localHeader + 30 + buffer.readUInt16LE(entry.localHeader + 26) + buffer.readUInt16LE(entry.localHeader + 28)
      const data = buffer.subarray(start, start + entry.compressedSize)
      return (entry.method === 8 ? inflateRawSync(data) : data).toString('utf8')
    }
  }
}

const unescapeXml = text => text
  .replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&apos;/g, "'").replace(/&amp;/g, '&')

// OpenXML writers may prefix every element (<x:row>), so each element name allows one.
const tag = name => `(?:\\w+:)?${name}`
const valueOf = body => new RegExp(`<${tag('v')}>([^<]*)</${tag('v')}>`).exec(body)?.[1]

const textOf = xml => unescapeXml([...xml.matchAll(new RegExp(`<${tag('t')}(?:\\s[^>]*)?>([\\s\\S]*?)</${tag('t')}>`, 'g'))]
  .map(m => m[1]).join(''))

/** The text of row 1 of the workbook's first sheet, cell by cell. */
export function xlsxHeaderRow(buffer) {
  const zip = zipEntries(buffer)
  const sheet = zip.read('xl/worksheets/sheet1.xml')
  if (!sheet) throw new Error('No first worksheet')
  const shared = [...(zip.read('xl/sharedStrings.xml') ?? '').matchAll(new RegExp(`<${tag('si')}>([\\s\\S]*?)</${tag('si')}>`, 'g'))]
    .map(m => textOf(m[1]))
  const row = new RegExp(`<${tag('row')}\\b[^>]*\\br="1"[^>]*>([\\s\\S]*?)</${tag('row')}>`).exec(sheet)
  if (!row) return []
  return [...row[1].matchAll(new RegExp(`<${tag('c')}\\b([^>]*?)(?:/>|>([\\s\\S]*?)</${tag('c')}>)`, 'g'))].map(([, attributes, body = '']) => {
    const type = /\bt="([^"]+)"/.exec(attributes)?.[1]
    if (type === 's') return shared[Number(valueOf(body))]
    if (type === 'inlineStr') return textOf(body)
    return unescapeXml(valueOf(body) ?? '')
  })
}
