import type { components } from './schema'

export type JobAccepted = components['schemas']['JobAccepted']
export type JobDto = components['schemas']['JobDto']
export type JobStepDto = components['schemas']['JobStepDto']

export type JobEvent =
  | { name: 'job.step'; data: JobStepDto; id?: string }
  | { name: 'job.updated'; data: JobDto; id?: string }
  | { name: 'job.completed'; data: JobDto; id?: string }
  | { name: 'stream.closing'; data: { reason?: string }; id?: string }

/**
 * Subscribe to a job's eventsUrl. EventSource sends cookies same-origin and
 * resumes via Last-Event-ID on reconnect.
 */
export function subscribeJobEvents(
  eventsUrl: string,
  onEvent: (event: JobEvent) => void,
): () => void {
  const source = new EventSource(eventsUrl, { withCredentials: true })

  const bind = (name: JobEvent['name']) => {
    source.addEventListener(name, (ev: MessageEvent<string>) => {
      try {
        onEvent({ name, data: JSON.parse(ev.data), id: ev.lastEventId } as JobEvent)
      } catch {
        /* ignore malformed frames */
      }
    })
  }

  bind('job.step')
  bind('job.updated')
  bind('job.completed')
  bind('stream.closing')

  return () => source.close()
}
