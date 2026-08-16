/**
 * Checks `lib/qr.ts` by decoding everything it produces.
 *
 * A QR code that is subtly wrong still renders as a plausible square of noise,
 * so looking at one proves nothing, and neither does scanning the single
 * example that happened to work. This renders each matrix to a bitmap and reads
 * it back with decoders that share no code with the encoder. A payload that
 * survives the round trip has had its encoding, error correction, module
 * placement, mask selection and format bits all exercised by something with no
 * interest in agreeing.
 *
 * Every payload length from 1 to 213 bytes, which covers versions 1 to 10, both
 * character-count widths, every block-splitting arrangement, and the padding
 * edge cases at each version boundary — plus the provisioning URIs the API
 * actually builds, at several account-name lengths.
 *
 * Two decoders, and a symbol passes if either reads it. That is not a way of
 * lowering the bar: both are whole-pipeline readers, and a matrix that is
 * genuinely malformed is unreadable by both. They are here because each one
 * intermittently fails to *locate* an otherwise valid symbol depending on the
 * render scale — during development the base detector missed seven symbols that
 * the WeChat detector read exactly, and the WeChat detector missed one that the
 * base detector read exactly. Requiring both would report encoder bugs that are
 * not there.
 *
 * Not part of the test suite: it needs Python, NumPy and OpenCV with contrib,
 * which the dashboard's build image does not have. Run it when `qr.ts` changes.
 *
 *   pip3 install numpy opencv-contrib-python-headless
 *   node --experimental-strip-types lib/qr.verify.mjs
 *
 * A note on why this does not diff against a second encoder, which was tried
 * first and abandoned: segno appends a spurious zero codeword whenever the data
 * stream already ends on a codeword boundary, which in byte mode is almost
 * always, since the header is a fixed twelve bits. Both symbols decode the same
 * because the terminator ends the data either way, but the matrices differ, so
 * equality is the wrong test — matching it would mean reproducing the bug. ISO
 * 18004 section 7.4.10 adds padding bits only when the stream does not already
 * end at a boundary, which is what `qr.ts` implements.
 */
import { execFileSync } from 'node:child_process'
import { encodeQr } from './qr.ts'

const DECODER = `
import sys, json
import numpy as np, cv2

QUIET, SCALE = 6, 10
base = cv2.QRCodeDetector()
wechat = cv2.wechat_qrcode_WeChatQRCode()
out = []

for matrix in json.load(sys.stdin):
    size = len(matrix)
    img = np.ones((size + QUIET * 2, size + QUIET * 2), dtype=np.uint8) * 255
    for r, row in enumerate(matrix):
        for c, dark in enumerate(row):
            if dark:
                img[r + QUIET][c + QUIET] = 0

    big = cv2.resize(img, None, fx=SCALE, fy=SCALE, interpolation=cv2.INTER_NEAREST)
    results = []

    try:
        results.extend(wechat.detectAndDecode(cv2.cvtColor(big, cv2.COLOR_GRAY2BGR))[0])
    except cv2.error:
        pass
    try:
        text, _, _ = base.detectAndDecode(big)
        if text:
            results.append(text)
    except cv2.error:
        pass

    out.append(list(results))

json.dump(out, sys.stdout)
`

const ALPHABET = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789:/?&=@%.-_~'

/** Varied content, so masking and error correction see realistic bit patterns. */
const filler = (n) => {
  let text = ''
  for (let i = 0; i < n; i++) text += ALPHABET[(i * 7 + n * 13) % ALPHABET.length]
  return text
}

const uri = (account) =>
  `otpauth://totp/Airside:${account}` +
  '?secret=KRSXG5CTMVRXEZLUKN2XAZLSKNSWG23FMQ&issuer=Airside&algorithm=SHA1&digits=6&period=30'

const cases = []
for (let n = 1; n <= 213; n++) cases.push(filler(n))
for (const account of [
  'a@b.co',
  'tayoriye%40gmail.com',
  'ops%2Bairside@example.com',
  'an-unusually-long-administrator-address%40some-department.example.co.uk',
]) {
  cases.push(uri(account))
}

const matrices = cases.map((text) => encodeQr(text).map((row) => row.map(Boolean)))

const decoded = JSON.parse(
  execFileSync('python3', ['-c', DECODER], {
    input: JSON.stringify(matrices),
    encoding: 'utf8',
    maxBuffer: 1 << 29,
  }),
)

const failures = []
const versions = new Set()

cases.forEach((text, index) => {
  const version = (matrices[index].length - 17) / 4
  versions.add(version)

  if (!decoded[index].includes(text)) {
    failures.push({ version, length: text.length, decoded: decoded[index] })
  }
})

for (const failure of failures) {
  console.error(
    `FAIL v${failure.version}  ${failure.length} bytes  ` +
      (failure.decoded.length === 0
        ? 'neither decoder found a readable code'
        : `decoded as ${JSON.stringify(failure.decoded)}`),
  )
}

const versionList = [...versions].sort((a, b) => a - b).join(', ')

console.log(
  failures.length === 0
    ? `${cases.length} symbols across versions ${versionList} decoded back to their input.`
    : `\n${failures.length} of ${cases.length} did not round-trip.`,
)

process.exit(failures.length === 0 ? 0 : 1)
