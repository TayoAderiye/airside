import { Suspense } from 'react'
import { QueryView } from '@/components/databases/query-view'

export const metadata = { title: 'Query' }

export default function QueryPage() {
  return (
    <Suspense fallback={<p className="text-sm text-muted-foreground">Loading query…</p>}>
      <QueryView />
    </Suspense>
  )
}
