import { BackLink } from '@/components/ui/back-link'
import { LiveDatabaseDetail } from '@/components/databases/live-detail'

export const metadata = { title: 'Database' }

export default async function DatabaseDetailPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params
  return (
    <div className="flex flex-col gap-6">
      <BackLink href="/databases">Databases</BackLink>
      <LiveDatabaseDetail id={id} />
    </div>
  )
}
