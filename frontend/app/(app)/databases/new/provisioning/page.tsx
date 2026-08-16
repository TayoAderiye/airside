import { Suspense } from 'react'
import { Provisioning } from '@/components/databases/provisioning'

export const metadata = { title: 'Provisioning' }

export default function ProvisioningPage() {
  return (
    <Suspense fallback={<div className="text-sm text-muted-foreground">Loading…</div>}>
      <Provisioning />
    </Suspense>
  )
}
