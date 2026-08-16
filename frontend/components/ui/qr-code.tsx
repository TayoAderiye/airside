'use client'

import { useMemo } from 'react'

import { encodeQr } from '@/lib/qr'
import { cn } from '@/lib/utils'

/**
 * Renders a payload as a QR code, entirely in the browser.
 *
 * SVG rather than canvas so it stays sharp when a phone camera is held close to
 * a scaled display, and one path for all the dark modules rather than a rect
 * each, because a version 10 symbol is three thousand modules and that many
 * elements is a visible pause on a slow machine.
 *
 * Explicit black on white, ignoring the theme. A dark-mode inversion is
 * something some scanners read and others refuse, and this is on the path of
 * someone setting up their second factor — not where to find out which kind of
 * phone they have.
 */
export function QrCode({
  value,
  className,
  label,
}: {
  value: string
  className?: string
  label: string
}) {
  const { path, size } = useMemo(() => {
    const modules = encodeQr(value)
    const parts: string[] = []

    for (let r = 0; r < modules.length; r++) {
      for (let c = 0; c < modules.length; c++) {
        if (modules[r][c]) parts.push(`M${c} ${r}h1v1h-1z`)
      }
    }

    return { path: parts.join(''), size: modules.length }
  }, [value])

  // Four modules of quiet zone, which the spec requires and without which
  // scanners hunt for the symbol's edge against whatever is behind it.
  const quiet = 4
  const extent = size + quiet * 2

  return (
    <svg
      viewBox={`0 0 ${extent} ${extent}`}
      role="img"
      aria-label={label}
      shapeRendering="crispEdges"
      className={cn('h-44 w-44 rounded-md bg-white p-0', className)}
    >
      <rect width={extent} height={extent} fill="#fff" />
      <g transform={`translate(${quiet} ${quiet})`}>
        <path d={path} fill="#000" />
      </g>
    </svg>
  )
}
