import type { components } from '@/lib/api/schema'
import { cn } from '@/lib/utils'

type Check = components['schemas']['PreflightCheckDto']
type Report = components['schemas']['PreflightReportDto']

const TONE: Record<string, string> = {
  passed: 'border-running/40 bg-running-soft/40 text-running',
  unknown: 'border-transitional/40 bg-transitional-soft/40 text-transitional',
  warning: 'border-degraded/40 bg-degraded-soft/50 text-degraded',
  blocking: 'border-failed/40 bg-failed-soft/50 text-failed',
}

const MARK: Record<string, string> = {
  passed: '✓',
  unknown: '?',
  warning: '!',
  blocking: '✕',
}

/** Pre-flight is the main event — found / expected / remedy, not "validation failed". */
export function PreflightList({ report }: { report: Report }) {
  return (
    <div className="flex flex-col gap-2">
      <p className="font-mono text-xs text-muted-foreground">{report.hostname}</p>
      <ul className="flex flex-col gap-2">
        {report.checks.map((c) => (
          <PreflightRow key={c.id} check={c} />
        ))}
      </ul>
    </div>
  )
}

function PreflightRow({ check }: { check: Check }) {
  return (
    <li className={cn('rounded-lg border px-3 py-2', TONE[check.severity] ?? TONE.unknown)}>
      <p className="text-sm font-medium text-foreground">
        <span className="mr-2 font-mono">{MARK[check.severity] ?? '?'}</span>
        {check.summary}
      </p>
      {(check.found || check.expected) && (
        <p className="mt-1 font-mono text-xs text-foreground/80">
          {check.found != null && <span>found {check.found}</span>}
          {check.found && check.expected && ' · '}
          {check.expected != null && <span>expected {check.expected}</span>}
        </p>
      )}
      {check.remedy && <p className="mt-1 text-xs italic text-foreground/80">{check.remedy}</p>}
    </li>
  )
}
