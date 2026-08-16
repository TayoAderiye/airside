/**
 * Legacy assumed shapes used by leftover mock screens.
 * New UI imports generated types from ./schema (openapi-typescript) and
 * talks to the API through ./client. Do not add new assumed DTOs here.
 */

/** Every workload state the backend can report. Drives all status visuals. */
export type WorkloadState =
  | 'Provisioning'
  | 'Running'
  | 'Stopped'
  | 'Restarting'
  | 'BackingUp'
  | 'Restoring'
  | 'Failed'
  | 'Deleting'
  | 'Unhealthy'
  | 'Building'
  | 'Deploying'
  | 'RollingBack'

/** Transitional states read as "in motion" (pulse), not steady. */
export const TRANSITIONAL_STATES: WorkloadState[] = [
  'Provisioning',
  'Restarting',
  'BackingUp',
  'Restoring',
  'Deleting',
  'Building',
  'Deploying',
  'RollingBack',
]

export type StatusKind = 'running' | 'degraded' | 'failed' | 'stopped' | 'transitional'

export type DatabaseEngine = 'postgres' | 'mysql' | 'mongodb' | 'redis'

export type AppSourceKind = 'git' | 'image' | 'dockerfile' | 'compose'

export type MaxMemoryPolicy =
  | 'noeviction'
  | 'allkeys-lru'
  | 'allkeys-lfu'
  | 'volatile-lru'
  | 'volatile-lfu'
  | 'allkeys-random'
  | 'volatile-random'
  | 'volatile-ttl'

/** A single dimension of host capacity, in the resource's native unit. */
export interface ResourceTriple {
  /** What the host physically has. */
  capacity: number
  /** Sum of configured limits across all workloads. */
  allocated: number
  /** What is actually being consumed right now. */
  used: number
  /** Unit label for display, e.g. "cores", "GiB". */
  unit: string
}

export interface HostHealth {
  hostname: string
  uptimeSeconds: number
  loadAvg: [number, number, number]
  dockerConnected: boolean
  cpu: ResourceTriple
  memory: ResourceTriple
  storage: ResourceTriple
}

export interface DatabaseSummary {
  id: string
  name: string
  engine: DatabaseEngine
  version: string
  state: WorkloadState
  cpu: { used: number; limit: number } // cores
  memory: { used: number; limit: number } // GiB
  storage: { used: number; limit: number } // GiB
  connections: number
  uptimeSeconds: number
}

export interface RedisStats {
  memoryUsed: number // GiB
  maxMemory: number // GiB, 0 = unbounded
  maxMemoryPolicy: MaxMemoryPolicy
  aofEnabled: boolean
  hitRate: number // 0..1
  evictedKeys: number
  connectedClients: number
  keyspaceSize: number
}

export interface AppSummary {
  id: string
  name: string
  source: AppSourceKind
  repo?: string
  branch?: string
  image?: string
  state: WorkloadState
  replicas: number
  cpu: { used: number; limit: number }
  memory: { used: number; limit: number }
  internalPort: number
  domain?: string
  currentSha?: string
}

export interface Deployment {
  id: string
  appId: string
  appName: string
  sha: string
  branch: string
  message: string
  author: string
  startedAt: string
  finishedAt?: string
  durationSeconds?: number
  state: WorkloadState
  isCurrent: boolean
}

export interface Domain {
  id: string
  domain: string
  appId?: string
  appName?: string
  tlsStatus: 'active' | 'pending' | 'expiring' | 'expired' | 'none'
  issuer?: string
  expiresAt?: string
  autoRenew: boolean
}

export interface Secret {
  id: string
  key: string
  scope: string // e.g. app or "global"
  updatedAt: string
  updatedBy: string
}

export interface BackupPolicy {
  id: string
  resourceName: string
  resourceType: 'database' | 'application'
  schedule: string // cron-ish, human label
  retentionDays: number
  destination: 'local' | 's3'
  s3Bucket?: string
  compression: boolean
  encryption: boolean
  lastResult: 'success' | 'failed' | 'running' | 'never'
  lastRunAt?: string
}

export interface AuditEntry {
  id: string
  user: string
  action: string
  resource: string
  timestamp: string
  ip: string
  result: 'success' | 'denied' | 'failed'
  metadata?: Record<string, string>
}

export type RoleName =
  | 'Super Admin'
  | 'Infrastructure Admin'
  | 'Database Admin'
  | 'Application Admin'
  | 'Developer'
  | 'Read Only'

export interface AccessUser {
  id: string
  name: string
  email: string
  role: RoleName
  lastActive: string
  status: 'active' | 'invited' | 'disabled'
}

/** Granular permissions, grouped so infra vs. data access is visibly separate. */
export interface PermissionDef {
  key: string
  label: string
  group: 'Infrastructure' | 'Databases' | 'Applications' | 'Data & Query' | 'Secrets' | 'Access'
}

/** A live async operation (provision/build/deploy/backup/restore). */
export interface Job {
  id: string
  kind: 'Provisioning' | 'Building' | 'Deploying' | 'BackingUp' | 'Restoring'
  resourceName: string
  state: 'running' | 'succeeded' | 'failed'
  steps: JobStep[]
  startedAt: string
}

export interface JobStep {
  id: string
  label: string
  state: 'pending' | 'running' | 'succeeded' | 'failed'
  error?: string
}

export interface LogLine {
  id: number
  ts: string
  level: 'info' | 'warn' | 'error' | 'debug'
  message: string
  source: string
}

export type StreamState = 'connecting' | 'live' | 'stalled' | 'reconnecting' | 'closed' | 'error'

/* --------------------------------------------------------------------------
 * Infrastructure — assumed. v0 left these nav routes unwired. Shapes below
 * are what the screens consume; reconcile against the real v0.1 contract.
 * -------------------------------------------------------------------------- */

export interface ServerInfo {
  id: string
  hostname: string
  os: string
  kernel: string
  arch: string
  dockerVersion: string
  dockerSocket: string
  dockerConnected: boolean
  state: WorkloadState
  uptimeSeconds: number
  cpu: ResourceTriple
  memory: ResourceTriple
  storage: ResourceTriple
  loadAvg: [number, number, number]
}

export interface Volume {
  id: string
  name: string
  driver: 'local' | 's3'
  usedGiB: number
  limitGiB: number
  attachedTo?: string
  attachedType?: 'database' | 'application'
  mountPath?: string
  createdAt: string
}

export interface NetworkAttachment {
  id: string
  name: string
  kind: 'database' | 'application'
  ip?: string
}

export interface Network {
  id: string
  name: string
  driver: 'bridge' | 'host' | 'overlay'
  subnet: string
  gateway: string
  attached: NetworkAttachment[]
}

export interface BackupSnapshot {
  id: string
  policyId: string
  resourceName: string
  resourceType: 'database' | 'application'
  engine?: DatabaseEngine
  createdAt: string
  sizeGiB: number
  destination: 'local' | 's3'
  status: 'success' | 'failed'
}

export interface HostSettings {
  controlPlaneDomain: string
  tlsAuto: boolean
  tlsIssuer: string
  dockerSocket: string
  sessionTimeoutMinutes: number
  auditRetentionDays: number
  defaultBackupDestination: 'local' | 's3'
}

/** Assumed query endpoint. data.read for reads, data.write for mutations. Audited. */
export interface QueryResult {
  columns: string[]
  rows: Array<Record<string, string | number | boolean | null>>
  rowCount: number
  affected?: number
  truncated: boolean
  durationMs: number
  kind: 'read' | 'write' | 'command'
  message?: string
}
