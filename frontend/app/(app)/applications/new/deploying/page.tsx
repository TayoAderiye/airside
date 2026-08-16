import { Suspense } from 'react'
import { AppDeploying } from '@/components/applications/deploying'

export const metadata = { title: 'Deploying' }

export default function DeployingPage() {
  return (
    <Suspense fallback={<div className="text-sm text-muted-foreground">Loading…</div>}>
      <AppDeploying />
    </Suspense>
  )
}
