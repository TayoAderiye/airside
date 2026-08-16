import type { StatusKind, WorkloadState } from '@/lib/api/types'
import { TRANSITIONAL_STATES } from '@/lib/api/types'

interface StatusMeta {
  kind: StatusKind
  label: string
  /** Whether the state is a transition in progress (pulses in the UI). */
  transitional: boolean
}

const KIND_BY_STATE: Record<WorkloadState, StatusKind> = {
  Running: 'running',
  Unhealthy: 'degraded',
  Failed: 'failed',
  Stopped: 'stopped',
  Provisioning: 'transitional',
  Restarting: 'transitional',
  BackingUp: 'transitional',
  Restoring: 'transitional',
  Deleting: 'transitional',
  Building: 'transitional',
  Deploying: 'transitional',
  RollingBack: 'transitional',
}

const LABEL_BY_STATE: Record<WorkloadState, string> = {
  Running: 'Running',
  Unhealthy: 'Unhealthy',
  Failed: 'Failed',
  Stopped: 'Stopped',
  Provisioning: 'Provisioning',
  Restarting: 'Restarting',
  BackingUp: 'Backing up',
  Restoring: 'Restoring',
  Deleting: 'Deleting',
  Building: 'Building',
  Deploying: 'Deploying',
  RollingBack: 'Rolling back',
}

export function statusMeta(state: WorkloadState): StatusMeta {
  return {
    kind: KIND_BY_STATE[state],
    label: LABEL_BY_STATE[state],
    transitional: TRANSITIONAL_STATES.includes(state),
  }
}

/** Tailwind token classes per kind. Colour is always paired with a shape/label. */
export const KIND_CLASSES: Record<
  StatusKind,
  { dot: string; text: string; bg: string; border: string }
> = {
  running: {
    dot: 'bg-running',
    text: 'text-running',
    bg: 'bg-running-soft',
    border: 'border-running/25',
  },
  degraded: {
    dot: 'bg-degraded',
    text: 'text-degraded',
    bg: 'bg-degraded-soft',
    border: 'border-degraded/25',
  },
  failed: {
    dot: 'bg-failed',
    text: 'text-failed',
    bg: 'bg-failed-soft',
    border: 'border-failed/25',
  },
  stopped: {
    dot: 'bg-stopped',
    text: 'text-stopped',
    bg: 'bg-stopped-soft',
    border: 'border-stopped/25',
  },
  transitional: {
    dot: 'bg-transitional',
    text: 'text-transitional',
    bg: 'bg-transitional-soft',
    border: 'border-transitional/25',
  },
}

export function formatUptime(seconds: number): string {
  const d = Math.floor(seconds / 86400)
  const h = Math.floor((seconds % 86400) / 3600)
  const m = Math.floor((seconds % 3600) / 60)
  if (d > 0) return `${d}d ${h}h`
  if (h > 0) return `${h}h ${m}m`
  return `${m}m`
}

export function formatRelative(iso: string): string {
  const diff = Date.now() - new Date(iso).getTime()
  const s = Math.floor(diff / 1000)
  if (s < 60) return `${s}s ago`
  const m = Math.floor(s / 60)
  if (m < 60) return `${m}m ago`
  const h = Math.floor(m / 60)
  if (h < 24) return `${h}h ago`
  const d = Math.floor(h / 24)
  return `${d}d ago`
}
