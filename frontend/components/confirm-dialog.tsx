'use client'

import { useEffect, useId, useState } from 'react'
import { AlertTriangle, X } from 'lucide-react'

import { Button } from '@/components/ui/button'
import { TextInput } from '@/components/ui/field'
import { Mono } from '@/components/ui/panel'
import { cn } from '@/lib/utils'

export interface ConfirmExtra {
  id: string
  label: string
  description?: string
  danger?: boolean
  defaultChecked?: boolean
}

/**
 * Confirmation dialog for consequential actions.
 *
 * Interaction rules this enforces:
 *  - Destructive actions (tone="danger") require typing the resource name
 *    exactly (`requireTyped`), so a stray click can't destroy anything.
 *  - Extra decisions (e.g. also destroy the volume) are separate opt-in
 *    checkboxes, OFF by default — never merged into the primary click.
 *  - Warn-tone actions (stop/restart) are reversible, so they confirm on a
 *    single deliberate click with no typing.
 */
export function ConfirmDialog({
  open,
  onOpenChange,
  onConfirm,
  title,
  description,
  confirmLabel,
  tone = 'danger',
  requireTyped,
  extraConfirms = [],
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  onConfirm?: (extras: Record<string, boolean>) => void
  title: string
  description: React.ReactNode
  confirmLabel: string
  tone?: 'danger' | 'warn'
  requireTyped?: string
  extraConfirms?: ConfirmExtra[]
}) {
  const [typed, setTyped] = useState('')
  const [checks, setChecks] = useState<Record<string, boolean>>({})
  const inputId = useId()

  const close = () => onOpenChange(false)

  useEffect(() => {
    if (open) {
      setTyped('')
      setChecks(Object.fromEntries(extraConfirms.map((e) => [e.id, e.defaultChecked ?? false])))
    }
  }, [open]) // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    if (!open) return
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && close()
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [open]) // eslint-disable-line react-hooks/exhaustive-deps

  if (!open) return null

  const matches = !requireTyped || typed === requireTyped
  const isDanger = tone === 'danger'

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4" role="dialog" aria-modal="true" aria-label={title}>
      <button className="absolute inset-0 bg-background/80 backdrop-blur-sm" aria-label="Cancel" onClick={close} />
      <div
        className={cn(
          'relative w-full max-w-md rounded-lg border bg-card shadow-2xl',
          isDanger ? 'border-failed/40' : 'border-degraded/40',
        )}
      >
        <div className="flex items-start gap-3 border-b border-border px-4 py-3">
          <span
            className={cn(
              'mt-0.5 grid size-8 shrink-0 place-items-center rounded-md',
              isDanger ? 'bg-failed-soft text-failed' : 'bg-degraded-soft text-degraded',
            )}
          >
            <AlertTriangle className="size-4" />
          </span>
          <div className="min-w-0 flex-1">
            <h2 className="font-display text-sm font-semibold text-foreground">{title}</h2>
            <div className="mt-1 text-sm text-muted-foreground">{description}</div>
          </div>
          <button className="rounded-md p-1 text-muted-foreground hover:bg-secondary hover:text-foreground" aria-label="Cancel" onClick={close}>
            <X className="size-4" />
          </button>
        </div>

        {(extraConfirms.length > 0 || requireTyped) && (
          <div className="flex flex-col gap-4 px-4 py-4">
            {extraConfirms.length > 0 && (
              <div className="flex flex-col gap-2">
                {extraConfirms.map((extra) => (
                  <label
                    key={extra.id}
                    className={cn(
                      'flex cursor-pointer items-start gap-2.5 rounded-md border p-2.5',
                      checks[extra.id]
                        ? extra.danger
                          ? 'border-failed/50 bg-failed-soft/50'
                          : 'border-primary/50 bg-primary/5'
                        : 'border-border',
                    )}
                  >
                    <input
                      type="checkbox"
                      className="mt-0.5 size-4 accent-[var(--destructive)]"
                      checked={checks[extra.id] ?? false}
                      onChange={(e) => setChecks((c) => ({ ...c, [extra.id]: e.target.checked }))}
                    />
                    <span className="min-w-0">
                      <span className={cn('block text-sm font-medium', extra.danger ? 'text-failed' : 'text-foreground')}>
                        {extra.label}
                      </span>
                      {extra.description && (
                        <span className="block text-xs text-muted-foreground">{extra.description}</span>
                      )}
                    </span>
                  </label>
                ))}
              </div>
            )}

            {requireTyped && (
              <div className="flex flex-col gap-1.5">
                <label htmlFor={inputId} className="text-sm text-foreground">
                  Type <Mono className="rounded bg-secondary px-1 py-0.5 text-foreground">{requireTyped}</Mono> to confirm
                </label>
                <TextInput
                  id={inputId}
                  mono
                  autoFocus
                  autoComplete="off"
                  spellCheck={false}
                  value={typed}
                  onChange={(e) => setTyped(e.target.value)}
                  placeholder={requireTyped}
                />
              </div>
            )}
          </div>
        )}

        <div className="flex items-center justify-end gap-2 border-t border-border px-4 py-3">
          <Button variant="outline" onClick={close}>
            Cancel
          </Button>
          <Button
            variant={isDanger ? 'destructive' : 'default'}
            disabled={!matches}
            onClick={() => {
              if (!matches) return
              onConfirm?.(checks)
              close()
            }}
          >
            {confirmLabel}
          </Button>
        </div>
      </div>
    </div>
  )
}
