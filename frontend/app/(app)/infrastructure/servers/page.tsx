import { AllocationRail } from '@/components/allocation-rail'
import { HostVitals } from '@/components/dashboard/host-vitals'
import { StatusBadge } from '@/components/status-badge'
import { Mono, PageHeader, Panel, StatItem } from '@/components/ui/panel'
import { hostHealth, server } from '@/lib/api/mock'
import { formatUptime } from '@/lib/status'

export const metadata = { title: 'Servers' }

export default function ServersPage() {
  return (
    <div className="mx-auto max-w-6xl">
      <PageHeader
        title="Servers"
        description="This host. Airside manages a single Linux machine — there is no fleet view."
      />

      <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
        <div>
          <h2 className="font-display text-lg font-semibold text-foreground">{server.hostname}</h2>
          <Mono className="text-muted-foreground">
            {server.os} · {server.arch} · up {formatUptime(server.uptimeSeconds)}
          </Mono>
        </div>
        <StatusBadge state={server.state} />
      </div>

      <section aria-label="Host health" className="mb-6">
        <HostVitals host={hostHealth} />
      </section>

      <div className="grid grid-cols-1 gap-4 lg:grid-cols-3">
        <Panel title="Capacity" className="lg:col-span-2">
          <div className="flex flex-col gap-5">
            <AllocationRail label="CPU" triple={server.cpu} />
            <AllocationRail label="Memory" triple={server.memory} />
            <AllocationRail label="Storage" triple={server.storage} />
          </div>
        </Panel>

        <Panel title="Runtime">
          <div className="flex flex-col gap-3">
            <StatItem label="Kernel" value={server.kernel} mono />
            <StatItem label="Docker" value={server.dockerVersion} mono />
            <StatItem label="Socket" value={server.dockerSocket} mono />
            <StatItem
              label="Daemon"
              value={server.dockerConnected ? 'connected' : 'unreachable'}
              tone={server.dockerConnected ? 'good' : 'bad'}
            />
          </div>
        </Panel>
      </div>
    </div>
  )
}
