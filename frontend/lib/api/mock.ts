/**
 * Mock data + simulated SignalR for the ASSUMED contract (see ./types.ts).
 * Swap this module for the real client + @microsoft/signalr connection once
 * the v0.1 backend contract is available. Screens import only these shapes.
 */
import type {
  AccessUser,
  AppSummary,
  AuditEntry,
  BackupPolicy,
  BackupSnapshot,
  DatabaseSummary,
  Deployment,
  Domain,
  HostHealth,
  HostSettings,
  Job,
  LogLine,
  Network,
  QueryResult,
  PermissionDef,
  RedisStats,
  RoleName,
  Secret,
  ServerInfo,
  Volume,
} from './types'

export const hostHealth: HostHealth = {
  hostname: 'ip-10-0-3-14',
  uptimeSeconds: 1893600,
  loadAvg: [2.14, 1.87, 1.62],
  dockerConnected: true,
  cpu: { capacity: 8, allocated: 6.5, used: 4.9, unit: 'cores' },
  memory: { capacity: 32, allocated: 30, used: 12.4, unit: 'GiB' },
  storage: { capacity: 500, allocated: 240, used: 213, unit: 'GiB' },
}

export const databases: DatabaseSummary[] = [
  {
    id: 'db_pg_main',
    name: 'payments-primary',
    engine: 'postgres',
    version: '16.3',
    state: 'Running',
    cpu: { used: 1.2, limit: 2 },
    memory: { used: 3.1, limit: 8 },
    storage: { used: 64, limit: 100 },
    connections: 47,
    uptimeSeconds: 1209600,
  },
  {
    id: 'db_redis_cache',
    name: 'session-cache',
    engine: 'redis',
    version: '7.4',
    state: 'Running',
    cpu: { used: 0.3, limit: 1 },
    memory: { used: 1.6, limit: 2 },
    storage: { used: 0, limit: 0 },
    connections: 312,
    uptimeSeconds: 864000,
  },
  {
    id: 'db_mysql_analytics',
    name: 'analytics-store',
    engine: 'mysql',
    version: '8.4',
    state: 'Unhealthy',
    cpu: { used: 1.9, limit: 2 },
    memory: { used: 5.7, limit: 6 },
    storage: { used: 88, limit: 100 },
    connections: 12,
    uptimeSeconds: 432000,
  },
  {
    id: 'db_mongo_events',
    name: 'events-log',
    engine: 'mongodb',
    version: '7.0',
    state: 'BackingUp',
    cpu: { used: 0.8, limit: 2 },
    memory: { used: 2.2, limit: 4 },
    storage: { used: 40, limit: 80 },
    connections: 23,
    uptimeSeconds: 259200,
  },
  {
    id: 'db_pg_staging',
    name: 'staging-db',
    engine: 'postgres',
    version: '16.3',
    state: 'Stopped',
    cpu: { used: 0, limit: 1 },
    memory: { used: 0, limit: 2 },
    storage: { used: 12, limit: 40 },
    connections: 0,
    uptimeSeconds: 0,
  },
]

export const redisStats: Record<string, RedisStats> = {
  db_redis_cache: {
    memoryUsed: 1.6,
    maxMemory: 1.8,
    maxMemoryPolicy: 'allkeys-lru',
    aofEnabled: false,
    hitRate: 0.947,
    evictedKeys: 20481,
    connectedClients: 312,
    keyspaceSize: 1840233,
  },
}

export const apps: AppSummary[] = [
  {
    id: 'app_api',
    name: 'payments-api',
    source: 'git',
    repo: 'acme/payments-api',
    branch: 'main',
    state: 'Running',
    replicas: 3,
    cpu: { used: 1.4, limit: 3 },
    memory: { used: 2.8, limit: 6 },
    internalPort: 8080,
    domain: 'api.acme.internal',
    currentSha: 'a1b2c3d',
  },
  {
    id: 'app_web',
    name: 'dashboard-web',
    source: 'git',
    repo: 'acme/dashboard',
    branch: 'main',
    state: 'Unhealthy',
    replicas: 2,
    cpu: { used: 0.9, limit: 2 },
    memory: { used: 1.5, limit: 3 },
    internalPort: 3000,
    domain: 'app.acme.internal',
    currentSha: '9f8e7d6',
  },
  {
    id: 'app_worker',
    name: 'queue-worker',
    source: 'image',
    image: 'acme/worker:2.3.1',
    state: 'Deploying',
    replicas: 4,
    cpu: { used: 2.1, limit: 4 },
    memory: { used: 3.9, limit: 8 },
    internalPort: 0,
    currentSha: 'c4d5e6f',
  },
  {
    id: 'app_docs',
    name: 'docs-site',
    source: 'dockerfile',
    repo: 'acme/docs',
    branch: 'main',
    state: 'Stopped',
    replicas: 0,
    cpu: { used: 0, limit: 1 },
    memory: { used: 0, limit: 1 },
    internalPort: 80,
    domain: 'docs.acme.internal',
    currentSha: '1a2b3c4',
  },
]

export const deployments: Deployment[] = [
  {
    id: 'dep_1',
    appId: 'app_api',
    appName: 'payments-api',
    sha: 'a1b2c3d',
    branch: 'main',
    message: 'Add idempotency keys to charge endpoint',
    author: 'dana@acme.io',
    startedAt: new Date(Date.now() - 3600_000).toISOString(),
    finishedAt: new Date(Date.now() - 3400_000).toISOString(),
    durationSeconds: 203,
    state: 'Running',
    isCurrent: true,
  },
  {
    id: 'dep_2',
    appId: 'app_api',
    appName: 'payments-api',
    sha: 'f6e5d4c',
    branch: 'main',
    message: 'Bump webhook retry ceiling to 8',
    author: 'sam@acme.io',
    startedAt: new Date(Date.now() - 86400_000).toISOString(),
    finishedAt: new Date(Date.now() - 86400_000 + 240_000).toISOString(),
    durationSeconds: 240,
    state: 'Stopped',
    isCurrent: false,
  },
  {
    id: 'dep_3',
    appId: 'app_api',
    appName: 'payments-api',
    sha: '3c2b1a0',
    branch: 'main',
    message: 'Refactor connection pool sizing',
    author: 'dana@acme.io',
    startedAt: new Date(Date.now() - 2 * 86400_000).toISOString(),
    finishedAt: new Date(Date.now() - 2 * 86400_000 + 190_000).toISOString(),
    durationSeconds: 190,
    state: 'Stopped',
    isCurrent: false,
  },
  {
    id: 'dep_4',
    appId: 'app_api',
    appName: 'payments-api',
    sha: 'bad0001',
    branch: 'hotfix/timeout',
    message: 'Attempted timeout patch',
    author: 'sam@acme.io',
    startedAt: new Date(Date.now() - 3 * 86400_000).toISOString(),
    finishedAt: new Date(Date.now() - 3 * 86400_000 + 60_000).toISOString(),
    durationSeconds: 60,
    state: 'Failed',
    isCurrent: false,
  },
]

export const domains: Domain[] = [
  {
    id: 'dom_1',
    domain: 'api.acme.internal',
    appId: 'app_api',
    appName: 'payments-api',
    tlsStatus: 'active',
    issuer: "Let's Encrypt",
    expiresAt: new Date(Date.now() + 61 * 86400_000).toISOString(),
    autoRenew: true,
  },
  {
    id: 'dom_2',
    domain: 'app.acme.internal',
    appId: 'app_web',
    appName: 'dashboard-web',
    tlsStatus: 'expiring',
    issuer: "Let's Encrypt",
    expiresAt: new Date(Date.now() + 9 * 86400_000).toISOString(),
    autoRenew: true,
  },
  {
    id: 'dom_3',
    domain: 'legacy.acme.internal',
    tlsStatus: 'expired',
    issuer: "Let's Encrypt",
    expiresAt: new Date(Date.now() - 2 * 86400_000).toISOString(),
    autoRenew: false,
  },
  {
    id: 'dom_4',
    domain: 'docs.acme.internal',
    appId: 'app_docs',
    appName: 'docs-site',
    tlsStatus: 'pending',
    autoRenew: true,
  },
]

export const secrets: Secret[] = [
  { id: 's1', key: 'DATABASE_URL', scope: 'payments-api', updatedAt: new Date(Date.now() - 5 * 86400_000).toISOString(), updatedBy: 'dana@acme.io' },
  { id: 's2', key: 'STRIPE_SECRET_KEY', scope: 'payments-api', updatedAt: new Date(Date.now() - 30 * 86400_000).toISOString(), updatedBy: 'sam@acme.io' },
  { id: 's3', key: 'REDIS_PASSWORD', scope: 'global', updatedAt: new Date(Date.now() - 12 * 86400_000).toISOString(), updatedBy: 'dana@acme.io' },
  { id: 's4', key: 'JWT_SIGNING_KEY', scope: 'global', updatedAt: new Date(Date.now() - 90 * 86400_000).toISOString(), updatedBy: 'admin@acme.io' },
]

export const backups: BackupPolicy[] = [
  { id: 'b1', resourceName: 'payments-primary', resourceType: 'database', schedule: 'Daily at 02:00 UTC', retentionDays: 30, destination: 's3', s3Bucket: 'acme-backups', compression: true, encryption: true, lastResult: 'success', lastRunAt: new Date(Date.now() - 7 * 3600_000).toISOString() },
  { id: 'b2', resourceName: 'analytics-store', resourceType: 'database', schedule: 'Daily at 03:00 UTC', retentionDays: 14, destination: 's3', s3Bucket: 'acme-backups', compression: true, encryption: true, lastResult: 'failed', lastRunAt: new Date(Date.now() - 6 * 3600_000).toISOString() },
  { id: 'b3', resourceName: 'events-log', resourceType: 'database', schedule: 'Every 6 hours', retentionDays: 7, destination: 'local', compression: true, encryption: false, lastResult: 'running', lastRunAt: new Date(Date.now() - 120_000).toISOString() },
  { id: 'b4', resourceName: 'session-cache', resourceType: 'database', schedule: 'Manual only', retentionDays: 0, destination: 'local', compression: false, encryption: false, lastResult: 'never' },
]

export const auditEntries: AuditEntry[] = [
  { id: 'a1', user: 'dana@acme.io', action: 'database.restart', resource: 'payments-primary', timestamp: new Date(Date.now() - 600_000).toISOString(), ip: '10.0.3.14', result: 'success' },
  { id: 'a2', user: 'sam@acme.io', action: 'secret.reveal', resource: 'STRIPE_SECRET_KEY', timestamp: new Date(Date.now() - 1800_000).toISOString(), ip: '10.0.3.51', result: 'success', metadata: { reason: 'rotation' } },
  { id: 'a3', user: 'dev@acme.io', action: 'database.delete', resource: 'staging-db', timestamp: new Date(Date.now() - 3600_000).toISOString(), ip: '10.0.3.77', result: 'denied', metadata: { missing: 'databases.delete' } },
  { id: 'a4', user: 'dana@acme.io', action: 'app.deploy', resource: 'payments-api', timestamp: new Date(Date.now() - 5400_000).toISOString(), ip: '10.0.3.14', result: 'success', metadata: { sha: 'a1b2c3d' } },
  { id: 'a5', user: 'sam@acme.io', action: 'backup.restore', resource: 'analytics-store', timestamp: new Date(Date.now() - 7200_000).toISOString(), ip: '10.0.3.51', result: 'failed', metadata: { step: 'download', error: 'checksum mismatch' } },
]

export const accessUsers: AccessUser[] = [
  { id: 'u1', name: 'Dana Okafor', email: 'dana@acme.io', role: 'Super Admin', lastActive: new Date(Date.now() - 300_000).toISOString(), status: 'active' },
  { id: 'u2', name: 'Sam Reyes', email: 'sam@acme.io', role: 'Infrastructure Admin', lastActive: new Date(Date.now() - 1800_000).toISOString(), status: 'active' },
  { id: 'u3', name: 'Jordan Lee', email: 'jordan@acme.io', role: 'Database Admin', lastActive: new Date(Date.now() - 86400_000).toISOString(), status: 'active' },
  { id: 'u4', name: 'Priya Nair', email: 'priya@acme.io', role: 'Developer', lastActive: new Date(Date.now() - 3 * 86400_000).toISOString(), status: 'active' },
  { id: 'u5', name: 'Chris Vaughn', email: 'chris@acme.io', role: 'Read Only', lastActive: '', status: 'invited' },
]

export const permissions: PermissionDef[] = [
  { key: 'infra.view', label: 'View infrastructure', group: 'Infrastructure' },
  { key: 'infra.manage', label: 'Manage servers and networks', group: 'Infrastructure' },
  { key: 'databases.view', label: 'View databases', group: 'Databases' },
  { key: 'databases.lifecycle', label: 'Start, stop, restart databases', group: 'Databases' },
  { key: 'databases.resize', label: 'Resize CPU, memory, storage', group: 'Databases' },
  { key: 'databases.delete', label: 'Delete databases', group: 'Databases' },
  { key: 'apps.view', label: 'View applications', group: 'Applications' },
  { key: 'apps.deploy', label: 'Deploy and roll back', group: 'Applications' },
  { key: 'apps.scale', label: 'Scale and change limits', group: 'Applications' },
  { key: 'data.read', label: 'Read database contents', group: 'Data & Query' },
  { key: 'data.write', label: 'Run write queries', group: 'Data & Query' },
  { key: 'secrets.view', label: 'View secret keys', group: 'Secrets' },
  { key: 'secrets.reveal', label: 'Reveal secret values (audited)', group: 'Secrets' },
  { key: 'access.manage', label: 'Manage users and roles', group: 'Access' },
]

/** Role → permission keys. Note infra vs data are deliberately separable. */
export const rolePermissions: Record<RoleName, string[]> = {
  'Super Admin': permissions.map((p) => p.key),
  'Infrastructure Admin': ['infra.view', 'infra.manage', 'databases.view', 'databases.lifecycle', 'databases.resize', 'apps.view', 'apps.scale', 'secrets.view'],
  'Database Admin': ['databases.view', 'databases.lifecycle', 'databases.resize', 'databases.delete', 'data.read', 'data.write', 'secrets.view'],
  'Application Admin': ['apps.view', 'apps.deploy', 'apps.scale', 'databases.view', 'secrets.view'],
  Developer: ['apps.view', 'apps.deploy', 'databases.view', 'data.read'],
  'Read Only': ['infra.view', 'databases.view', 'apps.view'],
}

/* --------------------------------------------------------------------------
 * Simulated SignalR — logs hub + jobs hub. Mirrors the shape a real
 * @microsoft/signalr connection would expose (subscribe → callback → dispose).
 * -------------------------------------------------------------------------- */

const LOG_SAMPLES: Array<Pick<LogLine, 'level' | 'message' | 'source'>> = [
  { level: 'info', message: 'GET /v1/charges 200 in 34ms', source: 'payments-api' },
  { level: 'info', message: 'POST /v1/charges 201 in 88ms trace=7fa21c', source: 'payments-api' },
  { level: 'debug', message: 'pool: acquired connection 12/20', source: 'payments-api' },
  { level: 'warn', message: 'retrying webhook delivery attempt=3 endpoint=hooks.acme.io', source: 'payments-api' },
  { level: 'info', message: 'GET /health 200 in 2ms', source: 'payments-api' },
  { level: 'error', message: 'upstream timeout after 5000ms provider=card-network', source: 'payments-api' },
  { level: 'info', message: 'POST /v1/refunds 200 in 51ms', source: 'payments-api' },
  { level: 'debug', message: 'gc pause 4ms heap=214MB', source: 'payments-api' },
  { level: 'warn', message: 'slow query 1240ms SELECT * FROM ledger WHERE ...', source: 'payments-api' },
  { level: 'info', message: 'GET /v1/customers/cus_84f2 200 in 19ms', source: 'payments-api' },
]

let logCounter = 0

export function subscribeLogs(
  onLine: (line: LogLine) => void,
  opts: { intervalMs?: number } = {},
): () => void {
  const interval = opts.intervalMs ?? 180
  const timer = setInterval(() => {
    // burst of 1-4 lines to exercise virtualization + buffer cap
    const burst = 1 + Math.floor(Math.random() * 4)
    for (let i = 0; i < burst; i++) {
      const sample = LOG_SAMPLES[Math.floor(Math.random() * LOG_SAMPLES.length)]
      onLine({ id: logCounter++, ts: new Date().toISOString(), ...sample })
    }
  }, interval)
  return () => clearInterval(timer)
}

export const server: ServerInfo = {
  id: 'host_1',
  hostname: hostHealth.hostname,
  os: 'Ubuntu 24.04.2 LTS',
  kernel: '6.8.0-63-generic',
  arch: 'x86_64',
  dockerVersion: '27.1.1',
  dockerSocket: 'unix:///var/run/docker.sock',
  dockerConnected: hostHealth.dockerConnected,
  state: 'Running',
  uptimeSeconds: hostHealth.uptimeSeconds,
  cpu: hostHealth.cpu,
  memory: hostHealth.memory,
  storage: hostHealth.storage,
  loadAvg: hostHealth.loadAvg,
}

export const volumes: Volume[] = [
  {
    id: 'vol_pg_main',
    name: 'payments-primary-data',
    driver: 'local',
    usedGiB: 64,
    limitGiB: 100,
    attachedTo: 'payments-primary',
    attachedType: 'database',
    mountPath: '/var/lib/postgresql/data',
    createdAt: new Date(Date.now() - 120 * 86400_000).toISOString(),
  },
  {
    id: 'vol_mysql',
    name: 'analytics-store-data',
    driver: 'local',
    usedGiB: 88,
    limitGiB: 100,
    attachedTo: 'analytics-store',
    attachedType: 'database',
    mountPath: '/var/lib/mysql',
    createdAt: new Date(Date.now() - 90 * 86400_000).toISOString(),
  },
  {
    id: 'vol_mongo',
    name: 'events-log-data',
    driver: 'local',
    usedGiB: 40,
    limitGiB: 80,
    attachedTo: 'events-log',
    attachedType: 'database',
    mountPath: '/data/db',
    createdAt: new Date(Date.now() - 60 * 86400_000).toISOString(),
  },
  {
    id: 'vol_pg_staging',
    name: 'staging-db-data',
    driver: 'local',
    usedGiB: 12,
    limitGiB: 40,
    attachedTo: 'staging-db',
    attachedType: 'database',
    mountPath: '/var/lib/postgresql/data',
    createdAt: new Date(Date.now() - 30 * 86400_000).toISOString(),
  },
  {
    id: 'vol_api',
    name: 'payments-api-uploads',
    driver: 'local',
    usedGiB: 3.2,
    limitGiB: 20,
    attachedTo: 'payments-api',
    attachedType: 'application',
    mountPath: '/var/app/uploads',
    createdAt: new Date(Date.now() - 45 * 86400_000).toISOString(),
  },
  {
    id: 'vol_orphan',
    name: 'old-redis-dump',
    driver: 'local',
    usedGiB: 1.1,
    limitGiB: 4,
    createdAt: new Date(Date.now() - 200 * 86400_000).toISOString(),
  },
]

export const networks: Network[] = [
  {
    id: 'net_bridge',
    name: 'airside',
    driver: 'bridge',
    subnet: '172.18.0.0/16',
    gateway: '172.18.0.1',
    attached: [
      { id: 'db_pg_main', name: 'payments-primary', kind: 'database', ip: '172.18.0.10' },
      { id: 'db_redis_cache', name: 'session-cache', kind: 'database', ip: '172.18.0.11' },
      { id: 'app_api', name: 'payments-api', kind: 'application', ip: '172.18.0.20' },
      { id: 'app_web', name: 'dashboard-web', kind: 'application', ip: '172.18.0.21' },
      { id: 'app_worker', name: 'queue-worker', kind: 'application', ip: '172.18.0.22' },
    ],
  },
  {
    id: 'net_host',
    name: 'host',
    driver: 'host',
    subnet: '—',
    gateway: '—',
    attached: [],
  },
  {
    id: 'net_internal',
    name: 'db-internal',
    driver: 'bridge',
    subnet: '172.19.0.0/16',
    gateway: '172.19.0.1',
    attached: [
      { id: 'db_pg_main', name: 'payments-primary', kind: 'database', ip: '172.19.0.10' },
      { id: 'db_mysql_analytics', name: 'analytics-store', kind: 'database', ip: '172.19.0.12' },
      { id: 'db_mongo_events', name: 'events-log', kind: 'database', ip: '172.19.0.13' },
    ],
  },
]

export const snapshots: BackupSnapshot[] = [
  {
    id: 'snap_1',
    policyId: 'b1',
    resourceName: 'payments-primary',
    resourceType: 'database',
    engine: 'postgres',
    createdAt: new Date(Date.now() - 7 * 3600_000).toISOString(),
    sizeGiB: 18.4,
    destination: 's3',
    status: 'success',
  },
  {
    id: 'snap_2',
    policyId: 'b1',
    resourceName: 'payments-primary',
    resourceType: 'database',
    engine: 'postgres',
    createdAt: new Date(Date.now() - 31 * 3600_000).toISOString(),
    sizeGiB: 18.1,
    destination: 's3',
    status: 'success',
  },
  {
    id: 'snap_3',
    policyId: 'b2',
    resourceName: 'analytics-store',
    resourceType: 'database',
    engine: 'mysql',
    createdAt: new Date(Date.now() - 30 * 3600_000).toISOString(),
    sizeGiB: 42.0,
    destination: 's3',
    status: 'success',
  },
  {
    id: 'snap_4',
    policyId: 'b4',
    resourceName: 'session-cache',
    resourceType: 'database',
    engine: 'redis',
    createdAt: new Date(Date.now() - 14 * 86400_000).toISOString(),
    sizeGiB: 0.8,
    destination: 'local',
    status: 'success',
  },
]

export const hostSettings: HostSettings = {
  controlPlaneDomain: 'airside.acme.internal',
  tlsAuto: true,
  tlsIssuer: "Let's Encrypt",
  dockerSocket: 'unix:///var/run/docker.sock',
  sessionTimeoutMinutes: 60,
  auditRetentionDays: 365,
  defaultBackupDestination: 's3',
}

const WRITE_RE = /\b(insert|update|delete|drop|alter|truncate|create|replace|grant|revoke|flushall|flushdb|del|unlink|set|hset|lpush|rpush|sadd|zadd|expire|updateMany|deleteMany|insertOne|insertMany|updateOne|findOneAndUpdate|dropDatabase)\b/i

export function classifyStatement(statement: string): QueryResult['kind'] {
  const s = statement.trim()
  if (!s) return 'read'
  if (WRITE_RE.test(s)) return 'write'
  return /^(info|ping|echo|type|ttl|exists|scan|get|hget|lrange|smembers|zrange|dbsize|memory|client|find|aggregate|count|explain|show|desc|describe)\b/i.test(s)
    ? 'command'
    : 'read'
}

export function executeQuery(engine: DatabaseSummary['engine'], statement: string): Promise<QueryResult> {
  const started = performance.now()
  const kind = classifyStatement(statement)
  return new Promise((resolve, reject) => {
    window.setTimeout(() => {
      const durationMs = Math.round(performance.now() - started)
      const text = statement.trim()
      if (!text) {
        reject(new Error('Empty statement. Write a query and run it.'))
        return
      }
      if (/^keys\s+\*/i.test(text)) {
        reject(new Error('KEYS * is blocked. Use SCAN. KEYS walks the whole keyspace and will stall Redis under load.'))
        return
      }
      if (/\bdrop\s+(database|table|collection)\b/i.test(text)) {
        reject(new Error('Refused. Dropping a database or table is not available from Query — use Delete on the database.'))
        return
      }

      if (engine === 'redis') {
        resolve(redisResult(text, kind, durationMs))
        return
      }
      if (engine === 'mongodb') {
        resolve(mongoResult(text, kind, durationMs))
        return
      }
      resolve(sqlResult(engine, text, kind, durationMs))
    }, 280 + Math.floor(Math.random() * 220))
  })
}

function sqlResult(
  engine: DatabaseSummary['engine'],
  statement: string,
  kind: QueryResult['kind'],
  durationMs: number,
): QueryResult {
  if (kind === 'write') {
    return {
      columns: [],
      rows: [],
      rowCount: 0,
      affected: /\bdelete\b/i.test(statement) ? 3 : 1,
      truncated: false,
      durationMs,
      kind,
      message: 'Write applied. This run is in the audit log.',
    }
  }
  const rows =
    engine === 'mysql'
      ? [
          { id: 88421, event: 'page_view', user_id: 'usr_19', created_at: '2026-08-15 22:11:04' },
          { id: 88420, event: 'checkout_start', user_id: 'usr_04', created_at: '2026-08-15 22:10:51' },
          { id: 88419, event: 'charge_failed', user_id: 'usr_04', created_at: '2026-08-15 22:10:49' },
        ]
      : [
          { id: 'ch_7fa21c', status: 'succeeded', amount_cents: 4299, currency: 'usd', created_at: '2026-08-15 21:04:12' },
          { id: 'ch_3c2b1a', status: 'pending', amount_cents: 12000, currency: 'usd', created_at: '2026-08-15 21:03:58' },
          { id: 'ch_9f8e7d', status: 'failed', amount_cents: 750, currency: 'usd', created_at: '2026-08-15 20:55:01' },
        ]
  return {
    columns: Object.keys(rows[0]),
    rows,
    rowCount: rows.length,
    truncated: false,
    durationMs,
    kind: 'read',
  }
}

function mongoResult(statement: string, kind: QueryResult['kind'], durationMs: number): QueryResult {
  if (kind === 'write') {
    return {
      columns: [],
      rows: [],
      rowCount: 0,
      affected: 1,
      truncated: false,
      durationMs,
      kind,
      message: 'Write applied. This run is in the audit log.',
    }
  }
  const rows = [
    { _id: '66b1a0c1', type: 'charge.created', payload: '{amount:4299}', ts: '2026-08-15T21:04:12Z' },
    { _id: '66b1a0c0', type: 'charge.failed', payload: '{code:card_declined}', ts: '2026-08-15T20:55:01Z' },
  ]
  return {
    columns: Object.keys(rows[0]),
    rows,
    rowCount: rows.length,
    truncated: false,
    durationMs,
    kind: statement.trim().startsWith('db.') ? 'command' : 'read',
  }
}

function redisResult(statement: string, kind: QueryResult['kind'], durationMs: number): QueryResult {
  const cmd = statement.trim().split(/\s+/)[0]?.toUpperCase() ?? ''
  if (cmd === 'INFO' || cmd === 'DBSIZE') {
    return {
      columns: ['field', 'value'],
      rows: [
        { field: 'used_memory_human', value: '1.60G' },
        { field: 'maxmemory_human', value: '1.80G' },
        { field: 'keyspace_hits', value: 1840233 },
        { field: 'evicted_keys', value: 20481 },
        { field: 'connected_clients', value: 312 },
        { field: 'db0', value: 'keys=1840233,expires=1102' },
      ],
      rowCount: 6,
      truncated: false,
      durationMs,
      kind: 'command',
    }
  }
  if (kind === 'write') {
    return {
      columns: ['result'],
      rows: [{ result: cmd === 'SET' ? 'OK' : '1' }],
      rowCount: 1,
      affected: 1,
      truncated: false,
      durationMs,
      kind,
      message: 'Write applied. Connected clients see this immediately. This run is in the audit log.',
    }
  }
  return {
    columns: ['key', 'type', 'ttl', 'value'],
    rows: [
      { key: 'sess:7fa21c', type: 'string', ttl: 1840, value: '{"uid":"usr_04"}' },
      { key: 'sess:3c2b1a', type: 'string', ttl: 900, value: '{"uid":"usr_19"}' },
      { key: 'lock:charge:88421', type: 'string', ttl: 12, value: '1' },
    ],
    rowCount: 3,
    truncated: true,
    durationMs,
    kind: 'command',
    message: 'SCAN cursor 42. Not the full keyspace.',
  }
}

export function makeInitialJob(): Job {
  return {
    id: 'job_deploy_1',
    kind: 'Deploying',
    resourceName: 'payments-api',
    state: 'running',
    startedAt: new Date().toISOString(),
    steps: [
      { id: 'clone', label: 'Clone repository', state: 'succeeded' },
      { id: 'build', label: 'Build image', state: 'running' },
      { id: 'push', label: 'Push to registry', state: 'pending' },
      { id: 'migrate', label: 'Run migrations', state: 'pending' },
      { id: 'rollout', label: 'Roll out replicas', state: 'pending' },
      { id: 'health', label: 'Health check', state: 'pending' },
    ],
  }
}
