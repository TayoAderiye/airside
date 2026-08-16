import type { components } from '@/lib/api/schema'

type Warning = components['schemas']['WarningDto']

/** Advisory warnings stay on the resource. They are not toasts. */
export function WarningsList({ warnings }: { warnings?: Warning[] | null }) {
  if (!warnings?.length) return null
  return (
    <ul className="flex flex-col gap-2">
      {warnings.map((w) => (
        <li
          key={w.code}
          className="rounded-md border border-degraded/30 bg-degraded-soft/50 px-3 py-2"
        >
          <p className="font-mono text-[11px] text-degraded">{w.code}</p>
          <p className="text-sm text-foreground">{w.message}</p>
        </li>
      ))}
    </ul>
  )
}
