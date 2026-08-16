import { AppDetailView } from '@/components/applications/detail-view'

export function generateMetadata() {
  return { title: 'Application' }
}

/**
 * Nothing is fetched here on purpose.
 *
 * The API authenticates with a session cookie, which belongs to the browser —
 * a server-side fetch from this component would arrive without it. Everything
 * this page shows is loaded by the client view below.
 */
export default async function AppDetailPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params

  return <AppDetailView applicationId={id} />
}
