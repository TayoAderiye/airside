import { apps, databases } from '@/lib/api/mock'
import { MonitoringView } from '@/components/monitoring/monitoring-view'

export const metadata = { title: 'Monitoring' }

export default function MonitoringPage() {
  return <MonitoringView databases={databases} apps={apps} />
}
