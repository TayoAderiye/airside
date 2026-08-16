/**
 * A QR encoder, byte mode, error correction level M, versions 1 to 10.
 *
 * This exists rather than a dependency because of what it encodes. The only
 * thing Airside ever puts in a QR code is an `otpauth://` URI, and that URI
 * contains the TOTP shared secret in clear text — the one string on the whole
 * dashboard that must never reach a third party. A hosted chart API is
 * therefore out of the question, and a transitive dependency on the page that
 * renders it is a supply chain with the second authentication factor at the end
 * of it.
 *
 * Byte mode and level M only, because that is the entire requirement. Level M
 * recovers about 15% and is what every authenticator provisioning code uses;
 * version 10 holds 213 bytes, roughly twice the longest URI the API can build.
 * Modes and versions that will never be reached are not implemented, so there
 * is no untested code here pretending to work.
 *
 * Every symbol this produces, for every payload length from 1 to 213 bytes and
 * for the provisioning URIs the API actually builds, has been rendered and read
 * back by independent decoders. See `lib/qr.verify.mjs`.
 */

/** Error correction level M for each supported version. */
interface VersionSpec {
  /** Error correction codewords per block. */
  readonly ecPerBlock: number
  /** `[blockCount, dataCodewordsPerBlock]` for each of the one or two groups. */
  readonly groups: readonly (readonly [number, number])[]
  /** Row/column centres of the alignment patterns. */
  readonly alignment: readonly number[]
}

const VERSIONS: readonly VersionSpec[] = [
  { ecPerBlock: 10, groups: [[1, 16]], alignment: [] },
  { ecPerBlock: 16, groups: [[1, 28]], alignment: [6, 18] },
  { ecPerBlock: 26, groups: [[1, 44]], alignment: [6, 22] },
  { ecPerBlock: 18, groups: [[2, 32]], alignment: [6, 26] },
  { ecPerBlock: 24, groups: [[2, 43]], alignment: [6, 30] },
  { ecPerBlock: 16, groups: [[4, 27]], alignment: [6, 34] },
  { ecPerBlock: 18, groups: [[4, 31]], alignment: [6, 22, 38] },
  { ecPerBlock: 22, groups: [[2, 38], [2, 39]], alignment: [6, 24, 42] },
  { ecPerBlock: 22, groups: [[3, 36], [2, 37]], alignment: [6, 26, 46] },
  { ecPerBlock: 26, groups: [[4, 43], [1, 44]], alignment: [6, 28, 50] },
]

/** Log and antilog tables for GF(256) with the QR primitive polynomial 0x11d. */
const EXP = new Uint8Array(512)
const LOG = new Uint8Array(256)

for (let i = 0, x = 1; i < 255; i++) {
  EXP[i] = x
  LOG[x] = i
  x <<= 1
  if (x & 0x100) x ^= 0x11d
}
for (let i = 255; i < 512; i++) EXP[i] = EXP[i - 255]

function mul(a: number, b: number): number {
  return a === 0 || b === 0 ? 0 : EXP[LOG[a] + LOG[b]]
}

/** The generator polynomial for `degree` error correction codewords. */
function generator(degree: number): Uint8Array {
  let poly = new Uint8Array([1])

  for (let d = 0; d < degree; d++) {
    const next = new Uint8Array(poly.length + 1)

    for (let i = 0; i < poly.length; i++) {
      next[i] ^= poly[i]
      next[i + 1] ^= mul(poly[i], EXP[d])
    }
    poly = next
  }

  return poly
}

/** Polynomial long division; the remainder is the error correction block. */
function remainder(data: Uint8Array, degree: number): Uint8Array {
  const gen = generator(degree)
  const buffer = new Uint8Array(data.length + degree)
  buffer.set(data)

  for (let i = 0; i < data.length; i++) {
    const factor = buffer[i]
    if (factor === 0) continue

    for (let j = 0; j < gen.length; j++) {
      buffer[i + j] ^= mul(gen[j], factor)
    }
  }

  return buffer.slice(data.length)
}

class BitBuffer {
  readonly bits: number[] = []

  push(value: number, length: number): void {
    for (let i = length - 1; i >= 0; i--) {
      this.bits.push((value >>> i) & 1)
    }
  }
}

/**
 * Encodes the payload into codewords, then expands them into interleaved data
 * and error correction blocks.
 */
function codewords(bytes: Uint8Array, version: number): Uint8Array {
  const spec = VERSIONS[version - 1]
  const blocks = spec.groups.flatMap(([count, size]) => Array.from({ length: count }, () => size))
  const dataTotal = blocks.reduce((sum, size) => sum + size, 0)

  const buffer = new BitBuffer()
  buffer.push(0b0100, 4) // byte mode
  buffer.push(bytes.length, version < 10 ? 8 : 16)
  for (const b of bytes) buffer.push(b, 8)

  // Terminator, up to four bits, and only as many as still fit.
  const capacity = dataTotal * 8
  buffer.push(0, Math.min(4, capacity - buffer.bits.length))

  // Pad to a whole codeword, then alternate the two pad bytes the spec names.
  while (buffer.bits.length % 8 !== 0) buffer.bits.push(0)

  const data = new Uint8Array(dataTotal)
  for (let i = 0; i < buffer.bits.length; i += 8) {
    let byte = 0
    for (let j = 0; j < 8; j++) byte = (byte << 1) | buffer.bits[i + j]
    data[i / 8] = byte
  }
  for (let i = buffer.bits.length / 8, pad = 0; i < dataTotal; i++, pad++) {
    data[i] = pad % 2 === 0 ? 0xec : 0x11
  }

  // Split into blocks, compute error correction for each.
  const dataBlocks: Uint8Array[] = []
  const ecBlocks: Uint8Array[] = []

  for (let i = 0, offset = 0; i < blocks.length; i++) {
    const block = data.subarray(offset, offset + blocks[i])
    offset += blocks[i]
    dataBlocks.push(block)
    ecBlocks.push(remainder(block, spec.ecPerBlock))
  }

  // Interleave: one codeword from each block in turn. A burst of damage then
  // falls across several blocks rather than destroying one of them outright,
  // which is the whole reason for splitting into blocks at all.
  const out: number[] = []
  const longest = Math.max(...blocks)

  for (let i = 0; i < longest; i++) {
    for (const block of dataBlocks) {
      if (i < block.length) out.push(block[i])
    }
  }
  for (let i = 0; i < spec.ecPerBlock; i++) {
    for (const block of ecBlocks) out.push(block[i])
  }

  return new Uint8Array(out)
}

type Grid = (boolean | null)[][]

/** Finder patterns, separators, timing, alignment, and the reserved areas. */
function scaffold(version: number): { grid: Grid; reserved: boolean[][] } {
  const size = version * 4 + 17
  const grid: Grid = Array.from({ length: size }, () => Array<boolean | null>(size).fill(null))
  const reserved = Array.from({ length: size }, () => Array<boolean>(size).fill(false))

  const set = (r: number, c: number, dark: boolean) => {
    grid[r][c] = dark
    reserved[r][c] = true
  }

  // Three finder patterns with their separators.
  for (const [top, left] of [[0, 0], [0, size - 7], [size - 7, 0]]) {
    for (let r = -1; r <= 7; r++) {
      for (let c = -1; c <= 7; c++) {
        const y = top + r
        const x = left + c
        if (y < 0 || y >= size || x < 0 || x >= size) continue

        const inRing = (r === 0 || r === 6) && c >= 0 && c <= 6
        const inSide = (c === 0 || c === 6) && r >= 0 && r <= 6
        const inCore = r >= 2 && r <= 4 && c >= 2 && c <= 4
        set(y, x, inRing || inSide || inCore)
      }
    }
  }

  // Timing patterns, which give a scanner its module pitch.
  for (let i = 8; i < size - 8; i++) {
    set(6, i, i % 2 === 0)
    set(i, 6, i % 2 === 0)
  }

  // Alignment patterns, everywhere except overlapping a finder.
  const centres = VERSIONS[version - 1].alignment
  for (const r of centres) {
    for (const c of centres) {
      const nearFinder =
        (r <= 8 && c <= 8) || (r <= 8 && c >= size - 9) || (r >= size - 9 && c <= 8)
      if (nearFinder) continue

      for (let dr = -2; dr <= 2; dr++) {
        for (let dc = -2; dc <= 2; dc++) {
          set(r + dr, c + dc, Math.max(Math.abs(dr), Math.abs(dc)) !== 1)
        }
      }
    }
  }

  // The dark module, which is always set and has no meaning beyond being there.
  set(size - 8, 8, true)

  // Reserve the format areas; they are filled once a mask is chosen.
  for (let i = 0; i < 9; i++) {
    if (!reserved[8][i]) reserved[8][i] = true
    if (!reserved[i][8]) reserved[i][8] = true
  }
  for (let i = 0; i < 8; i++) {
    reserved[8][size - 1 - i] = true
    reserved[size - 1 - i][8] = true
  }

  // Version information, on version 7 and above.
  if (version >= 7) {
    let bits = version
    for (let i = 0; i < 12; i++) {
      bits = (bits << 1) ^ ((bits >>> 11) * 0x1f25)
    }
    const value = (version << 12) | bits

    for (let i = 0; i < 18; i++) {
      const bit = ((value >>> i) & 1) === 1
      const r = Math.floor(i / 3)
      const c = size - 11 + (i % 3)
      set(r, c, bit)
      set(c, r, bit)
    }
  }

  return { grid, reserved }
}

/** The eight mask conditions, indexed by mask number. */
const MASKS: readonly ((r: number, c: number) => boolean)[] = [
  (r, c) => (r + c) % 2 === 0,
  (r) => r % 2 === 0,
  (_r, c) => c % 3 === 0,
  (r, c) => (r + c) % 3 === 0,
  (r, c) => (Math.floor(r / 2) + Math.floor(c / 3)) % 2 === 0,
  (r, c) => ((r * c) % 2) + ((r * c) % 3) === 0,
  (r, c) => (((r * c) % 2) + ((r * c) % 3)) % 2 === 0,
  (r, c) => (((r + c) % 2) + ((r * c) % 3)) % 2 === 0,
]

/** Places the codeword stream in the two-column zigzag, skipping function areas. */
function place(grid: Grid, reserved: boolean[][], stream: Uint8Array): void {
  const size = grid.length
  let bit = 0

  for (let right = size - 1; right >= 1; right -= 2) {
    // Column 6 is entirely timing pattern, so the pairing shifts left past it.
    if (right === 6) right = 5

    for (let i = 0; i < size; i++) {
      const upward = ((right + 1) & 2) === 0
      const r = upward ? size - 1 - i : i

      for (const c of [right, right - 1]) {
        if (reserved[r][c]) continue

        const byte = stream[bit >>> 3]
        // Past the end of the stream the remainder bits are zero, which the
        // spec permits and scanners ignore.
        grid[r][c] = byte !== undefined && ((byte >>> (7 - (bit & 7))) & 1) === 1
        bit++
      }
    }
  }
}

/** Writes the 15-bit format string for level M and the given mask. */
function writeFormat(grid: Grid, mask: number): void {
  const size = grid.length
  let bits = (0b00 << 3) | mask // 0b00 is error correction level M

  let ec = bits
  for (let i = 0; i < 10; i++) {
    ec = (ec << 1) ^ ((ec >>> 9) * 0x537)
  }
  bits = ((bits << 10) | ec) ^ 0x5412

  for (let i = 0; i < 15; i++) {
    const bit = ((bits >>> i) & 1) === 1

    // The copy beside the top-left finder, skipping the timing row and column.
    if (i < 6) grid[i][8] = bit
    else if (i < 8) grid[i + 1][8] = bit
    else if (i === 8) grid[8][7] = bit
    else grid[8][14 - i] = bit

    // The second copy, split between the other two finders, so a corner lost to
    // damage does not take the format information with it.
    if (i < 8) grid[8][size - 1 - i] = bit
    else grid[size - 15 + i][8] = bit
  }
}

/**
 * Rule 3's contribution for one row or column.
 *
 * Two details decide the score, and both are easy to get wrong. Occurrences do
 * not overlap: after a match the scan resumes past it, so one stretch of modules
 * cannot be counted several times over. And the run of four light modules is
 * clamped at the edge of the symbol rather than required to fit inside it — a
 * pattern flush against the border counts, because there is nothing but quiet
 * zone beyond it.
 */
function finderLike(sequence: readonly boolean[]): number {
  const pattern = [true, false, true, true, true, false, true]
  const size = sequence.length

  const matchAt = (start: number) =>
    start + pattern.length <= size && pattern.every((want, k) => sequence[start + k] === want)

  const find = (from: number) => {
    for (let i = from; i + pattern.length <= size; i++) {
      if (matchAt(i)) return i
    }
    return -1
  }

  let score = 0

  for (let index = find(0); index !== -1; ) {
    const after = index + pattern.length
    const light = (from: number, to: number) =>
      !sequence.slice(Math.max(from, 0), Math.min(to, size)).some(Boolean)

    if (light(index - 4, index) || light(after, after + 4)) {
      score += 40
      index = find(after)
    } else {
      // No run of light either side. Resume from the middle of the pattern,
      // where the next one could still begin.
      index = find(index + 4)
    }
  }

  return score
}

/** The four penalty rules, which pick the mask that scans most reliably. */
function penalty(grid: Grid): number {
  const size = grid.length
  const at = (r: number, c: number) => grid[r][c] === true
  let score = 0

  // Rule 1: runs of five or more of the same colour, in both directions.
  for (let i = 0; i < size; i++) {
    for (const horizontal of [true, false]) {
      let run = 1

      for (let j = 1; j < size; j++) {
        const prev = horizontal ? at(i, j - 1) : at(j - 1, i)
        const cur = horizontal ? at(i, j) : at(j, i)

        if (cur === prev) {
          run++
          continue
        }
        if (run >= 5) score += run - 2
        run = 1
      }
      if (run >= 5) score += run - 2
    }
  }

  // Rule 2: every 2x2 block of one colour.
  for (let r = 0; r < size - 1; r++) {
    for (let c = 0; c < size - 1; c++) {
      const v = at(r, c)
      if (v === at(r, c + 1) && v === at(r + 1, c) && v === at(r + 1, c + 1)) score += 3
    }
  }

  // Rule 3: the finder-like 1:1:3:1:1 sequence with four light modules beside
  // it, which a scanner can mistake for a finder pattern.
  for (let i = 0; i < size; i++) {
    const row: boolean[] = []
    const column: boolean[] = []

    for (let j = 0; j < size; j++) {
      row.push(at(i, j))
      column.push(at(j, i))
    }

    score += finderLike(row) + finderLike(column)
  }

  // Rule 4: how far the proportion of dark modules strays from half.
  let dark = 0
  for (let r = 0; r < size; r++) for (let c = 0; c < size; c++) if (at(r, c)) dark++

  const percent = (dark * 100) / (size * size)
  score += Math.floor(Math.abs(percent - 50) / 5) * 10

  return score
}

/**
 * Encodes `text` and returns the module matrix, dark modules being `true`.
 *
 * Throws when the payload is longer than version 10 at level M can hold, which
 * for the provisioning URIs this is used for would take an account name of
 * about a hundred and thirty characters.
 *
 * `forceMask` pins the mask instead of scoring all eight. It exists so the
 * verifier can compare encoding and placement against the reference
 * independently of mask selection; nothing in the dashboard passes it.
 */
export function encodeQr(text: string, forceMask?: number): boolean[][] {
  const bytes = new TextEncoder().encode(text)

  const version = VERSIONS.findIndex((spec, index) => {
    const dataCodewords = spec.groups.reduce((sum, [count, size]) => sum + count * size, 0)
    const countBits = index + 1 < 10 ? 8 : 16
    return bytes.length * 8 + 4 + countBits <= dataCodewords * 8
  }) + 1

  if (version === 0) {
    throw new Error(`${bytes.length} bytes is too long for a version 10 QR code.`)
  }

  const stream = codewords(bytes, version)

  let best: boolean[][] | null = null
  let bestScore = Infinity

  const masks = forceMask === undefined ? [0, 1, 2, 3, 4, 5, 6, 7] : [forceMask]

  for (const mask of masks) {
    const { grid, reserved } = scaffold(version)
    place(grid, reserved, stream)

    for (let r = 0; r < grid.length; r++) {
      for (let c = 0; c < grid.length; c++) {
        if (!reserved[r][c] && MASKS[mask](r, c)) grid[r][c] = !grid[r][c]
      }
    }

    writeFormat(grid, mask)

    const candidate = grid.map((row) => row.map((cell) => cell === true))
    const score = penalty(candidate)

    if (score < bestScore) {
      bestScore = score
      best = candidate
    }
  }

  return best!
}
