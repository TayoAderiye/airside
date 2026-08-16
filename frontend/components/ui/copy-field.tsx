'use client'

import { useState } from 'react'
import { Check, Copy, Eye, EyeOff } from 'lucide-react'
import { cn } from '@/lib/utils'

/**
 * Monospace value with copy-to-clipboard. Secrets/connection strings can be
 * masked by default and revealed deliberately, so a shoulder-surfer or a
 * screenshare doesn't leak them.
 */
export function CopyField({
  value,
  masked = false,
  className,
}: {
  value: string
  masked?: boolean
  className?: string
}) {
  const [copied, setCopied] = useState(false)
  const [revealed, setRevealed] = useState(!masked)

  async function copy() {
    try {
      await navigator.clipboard.writeText(value)
      setCopied(true)
      setTimeout(() => setCopied(false), 1400)
    } catch {
      /* clipboard unavailable */
    }
  }

  return (
    <div
      className={cn(
        'flex items-center gap-1 rounded-md border border-input bg-background pl-3 pr-1',
        className,
      )}
    >
      <code className="flex-1 overflow-x-auto whitespace-nowrap py-2 font-mono text-xs text-foreground">
        {revealed ? value : '•'.repeat(Math.min(48, value.length))}
      </code>
      {masked && (
        <button
          type="button"
          onClick={() => setRevealed((r) => !r)}
          aria-label={revealed ? 'Hide value' : 'Reveal value'}
          className="inline-flex size-7 items-center justify-center rounded text-muted-foreground hover:bg-accent hover:text-foreground"
        >
          {revealed ? <EyeOff className="size-3.5" /> : <Eye className="size-3.5" />}
        </button>
      )}
      <button
        type="button"
        onClick={copy}
        aria-label="Copy to clipboard"
        className="inline-flex size-7 items-center justify-center rounded text-muted-foreground hover:bg-accent hover:text-foreground"
      >
        {copied ? <Check className="size-3.5 text-running" /> : <Copy className="size-3.5" />}
      </button>
    </div>
  )
}
