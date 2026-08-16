/** RFC 9457 Problem Details as Airside returns them. */

export interface ProblemDetails {
  type?: string
  title?: string
  status: number
  detail: string
  code?: string
  metadata?: Record<string, unknown>
  traceId?: string
}

export class ApiError extends Error {
  readonly problem: ProblemDetails

  constructor(problem: ProblemDetails) {
    super(problem.detail || problem.title || `Request failed (${problem.status})`)
    this.name = 'ApiError'
    this.problem = problem
  }

  get status() {
    return this.problem.status
  }

  get code() {
    return this.problem.code
  }

  get metadata() {
    return this.problem.metadata ?? {}
  }

  get confirmField() {
    const v = this.metadata.confirmField
    return typeof v === 'string' ? v : undefined
  }

  get expected() {
    const v = this.metadata.expected
    return typeof v === 'string' ? v : undefined
  }
}

export function parseProblem(status: number, body: unknown): ProblemDetails {
  if (body && typeof body === 'object') {
    const o = body as Record<string, unknown>
    return {
      type: typeof o.type === 'string' ? o.type : undefined,
      title: typeof o.title === 'string' ? o.title : undefined,
      status: typeof o.status === 'number' ? o.status : status,
      detail: typeof o.detail === 'string' ? o.detail : `Request failed (${status})`,
      code: typeof o.code === 'string' ? o.code : undefined,
      metadata: o.metadata && typeof o.metadata === 'object' ? (o.metadata as Record<string, unknown>) : undefined,
      traceId: typeof o.traceId === 'string' ? o.traceId : undefined,
    }
  }
  return { status, detail: `Request failed (${status})` }
}
