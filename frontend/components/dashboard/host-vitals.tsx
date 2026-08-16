import { Activity, Clock, Cpu, HardDrive, MemoryStick } from 'lucide-react'

import type { HostHealth } from '@/lib/api/types'
import { formatUptime } from '@/lib/status'
import { Panel } from '@/components/ui/panel'
import { Mono } from '@/components/ui/panel'
import { cn } from '@/lib/utils'

function ratioTone(r: number) {
  if (r >= 0.9) return 'text-failed'
  if (r >= 0.75) return 'text-degraded'
  return 'text-running'
}

export function HostVitals({ host }: { host: HostHealth }) {
  const cpuR = host.cpu.used / host.cpu.capacity
  const memR = host.memory.used / host.memory.capacity
  const stoR = host.storage.used / host.storage.capacity

  return (
    <div className="grid grid-cols-2 gap-3 lg:grid-cols-5">
      <Vital icon={Cpu} label="CPU" value={`${Math.round(cpuR * 100)}%`} tone={ratioTone(cpuR)} sub={`${host.cpu.used} / ${host.cpu.capacity} cores`} />
      <Vital icon={MemoryStick} label="Memory" value={`${Math.round(memR * 100)}%`} tone={ratioTone(memR)} sub={`${host.memory.used} / ${host.memory.capacity} GiB`} />
      <Vital icon={HardDrive} label="Storage" value={`${Math.round(stoR * 100)}%`} tone={ratioTone(stoR)} sub={`${host.storage.used} / ${host.storage.capacity} GiB`} />
      <Vital icon={Activity} label="Load (1m)" value={host.loadAvg[0].toFixed(2)} tone={host.loadAvg[0] > host.cpu.capacity ? 'text-degraded' : 'text-foreground'} sub={`${host.loadAvg[1].toFixed(2)} · ${host.loadAvg[2].toFixed(2)}`} />
      <Vital icon={Clock} label="Uptime" value={formatUptime(host.uptimeSeconds)} tone="text-foreground" sub="since last reboot" />
    </div>
  )
}

function Vital({
  icon: Icon,
  label,
  value,
  tone,
  sub,
}: {
  icon: React.ComponentType<{ className?: string }>
  label: string
  value: string
  tone: string
  sub: string
}) {
  return (
    <Panel className="px-3 py-3">
      <div className="flex items-center gap-1.5 text-muted-foreground">
        <Icon className="size-3.5" />
        <span className="text-xs">{label}</span>
      </div>
      <p className={cn('mt-1.5 font-display text-2xl font-semibold tabular-nums', tone)}>{value}</p>
      <Mono className="mt-0.5 block text-muted-foreground">{sub}</Mono>
    </Panel>
  )
}
