import { ApiError } from '@/lib/api/problem'

/** Show ProblemDetails.detail as written. Switch on code for logic, not copy. */
export function ProblemBanner({ error }: { error: unknown }) {
  if (!error) return null
  const problem = error instanceof ApiError ? error.problem : null
  const detail = problem?.detail ?? (error instanceof Error ? error.message : 'Request failed.')
  return (
    <div className="rounded-lg border border-failed/40 bg-failed-soft/50 px-4 py-3">
      {problem?.code && <p className="font-mono text-[11px] text-failed">{problem.code}</p>}
      <p className="text-sm text-foreground">{detail}</p>
      {problem?.metadata && (
        <dl className="mt-2 grid grid-cols-[auto_1fr] gap-x-3 gap-y-0.5 font-mono text-xs text-muted-foreground">
          {Object.entries(problem.metadata).map(([k, v]) => (
            <span key={k} className="contents">
              <dt>{k}</dt>
              <dd className="text-foreground">{typeof v === 'string' ? v : JSON.stringify(v)}</dd>
            </span>
          ))}
        </dl>
      )}
    </div>
  )
}
