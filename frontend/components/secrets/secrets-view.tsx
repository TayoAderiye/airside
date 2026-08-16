'use client'

import { useState } from 'react'
import { Eye, EyeOff, KeyRound, Globe, Box } from 'lucide-react'
import type { Secret } from '@/lib/api/types'
import { ConfirmDialog } from '@/components/confirm-dialog'
import { PageHeader, Panel } from '@/components/ui/panel'
import { Button } from '@/components/ui/button'
import { formatRelative } from '@/lib/status'
import { cn } from '@/lib/utils'

export function SecretsView({ secrets }: { secrets: Secret[] }) {
  // Revealing a value is an audited action — track which are revealed locally
  // and surface that it is logged. Values are masked by default.
  const [revealed, setRevealed] = useState<Record<string, boolean>>({})
  const [pending, setPending] = useState<Secret | null>(null)

  return (
    <div className="flex flex-col gap-5">
      <PageHeader
        title="Secrets"
        description="Encrypted at rest. Revealing a value is recorded in the audit log."
        actions={<Button variant="default">Add secret</Button>}
      />

      <Panel bodyClassName="p-0">
        <ul className="divide-y divide-border">
          {secrets.map((s) => {
            const isRevealed = revealed[s.id]
            const global = s.scope === 'global'
            return (
              <li key={s.id} className="flex flex-col gap-3 p-4 sm:flex-row sm:items-center sm:gap-4">
                <div className="flex min-w-0 flex-1 items-center gap-3">
                  <span className="grid size-9 shrink-0 place-items-center rounded-md bg-secondary text-muted-foreground">
                    <KeyRound className="size-4" />
                  </span>
                  <div className="min-w-0">
                    <p className="truncate font-mono text-sm text-foreground">{s.key}</p>
                    <p className="flex items-center gap-1.5 text-xs text-muted-foreground">
                      {global ? <Globe className="size-3" /> : <Box className="size-3" />}
                      {s.scope}
                      <span aria-hidden>·</span>
                      updated {formatRelative(s.updatedAt)} by {s.updatedBy}
                    </p>
                  </div>
                </div>

                <div className="flex shrink-0 items-center gap-3 pl-12 sm:pl-0">
                  <code
                    className={cn(
                      'rounded bg-secondary px-2 py-1 font-mono text-xs',
                      isRevealed ? 'text-foreground' : 'text-muted-foreground',
                    )}
                  >
                    {isRevealed ? 'sk_live_4f2a…c91d' : '••••••••••••'}
                  </code>
                  <Button
                    variant="outline"
                    size="sm"
                    onClick={() => {
                      if (isRevealed) {
                        setRevealed((p) => ({ ...p, [s.id]: false }))
                        return
                      }
                      setPending(s)
                    }}
                    aria-pressed={isRevealed}
                  >
                    {isRevealed ? <EyeOff className="size-3.5" /> : <Eye className="size-3.5" />}
                    {isRevealed ? 'Hide' : 'Reveal'}
                  </Button>
                </div>
              </li>
            )
          })}
        </ul>
      </Panel>

      <p className="text-xs text-muted-foreground">
        Values shown are illustrative. Reveal fetches the decrypted value on demand and writes an audit entry.
      </p>

      <ConfirmDialog
        open={pending != null}
        onOpenChange={(open) => !open && setPending(null)}
        tone="warn"
        title={`Reveal ${pending?.key ?? 'secret'}?`}
        description="This writes an audit entry with your name, IP, and timestamp before the value is shown. Anyone with audit access will see that you revealed it."
        confirmLabel="Reveal and log"
        onConfirm={() => {
          if (pending) setRevealed((p) => ({ ...p, [pending.id]: true }))
          setPending(null)
        }}
      />
    </div>
  )
}
