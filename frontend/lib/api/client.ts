import createClient from 'openapi-fetch'

import { ApiError, parseProblem } from './problem'
import type { paths } from './schema'

/**
 * Generated OpenAPI client. Same-origin in the browser so the session cookie
 * is sent; Next rewrites /api and /openapi to the API process.
 */
export const client = createClient<paths>({
  baseUrl: '',
  credentials: 'include',
})

client.use({
  async onResponse({ response }) {
    if (response.ok) return response
    let body: unknown
    try {
      body = await response.clone().json()
    } catch {
      body = undefined
    }
    throw new ApiError(parseProblem(response.status, body))
  },
})

export type { paths }
