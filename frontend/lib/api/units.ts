import type { components } from './schema'
import type { WorkloadState } from './types'

export type ResourceTripleDto = components['schemas']['ResourceTripleDto']

export function asNumber(value: number | string | null | undefined) {
  if (value == null) return 0
  return typeof value === 'number' ? value : Number(value)
}

export function nanosToCores(nanos: number | string | null | undefined) {
  return asNumber(nanos) / 1_000_000_000
}

export function bytesToGiB(bytes: number | string | null | undefined) {
  return asNumber(bytes) / 1024 ** 3
}

export function coresToNanos(cores: number) {
  return Math.round(cores * 1_000_000_000)
}

export function giBToBytes(gib: number) {
  return Math.round(gib * 1024 ** 3)
}

export function tripleToRail(t: ResourceTripleDto) {
  return {
    capacity: nanosToCores(t.cpuNanos ?? 0),
    allocated: 0,
    used: 0,
    unit: 'cores',
  }
}

export function memoryRail(capacity: ResourceTripleDto, allocated: ResourceTripleDto, used?: ResourceTripleDto | null) {
  return {
    capacity: bytesToGiB(capacity.memoryBytes ?? 0),
    allocated: bytesToGiB(allocated.memoryBytes ?? 0),
    used: bytesToGiB(used?.memoryBytes ?? 0),
    unit: 'GiB',
  }
}

export function cpuRail(capacity: ResourceTripleDto, allocated: ResourceTripleDto, used?: ResourceTripleDto | null) {
  return {
    capacity: nanosToCores(capacity.cpuNanos ?? 0),
    allocated: nanosToCores(allocated.cpuNanos ?? 0),
    used: nanosToCores(used?.cpuNanos ?? 0),
    unit: 'cores',
  }
}

export function storageRail(capacity: ResourceTripleDto, allocated: ResourceTripleDto, used?: ResourceTripleDto | null) {
  return {
    capacity: bytesToGiB(capacity.storageBytes ?? 0),
    allocated: bytesToGiB(allocated.storageBytes ?? 0),
    used: bytesToGiB(used?.storageBytes ?? 0),
    unit: 'GiB',
  }
}

/** API states are camelCase; status visuals use PascalCase. */
export function apiState(state?: string | null): WorkloadState {
  if (!state) return 'Stopped'
  const key = state.charAt(0).toUpperCase() + state.slice(1)
  const known: WorkloadState[] = [
    'Provisioning',
    'Running',
    'Stopped',
    'Restarting',
    'BackingUp',
    'Restoring',
    'Failed',
    'Deleting',
    'Unhealthy',
    'Building',
    'Deploying',
    'RollingBack',
  ]
  return (known as string[]).includes(key) ? (key as WorkloadState) : 'Unhealthy'
}
