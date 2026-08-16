import { notFound } from 'next/navigation'
import { apps, deployments } from '@/lib/api/mock'
import { AppDetailView } from '@/components/applications/detail-view'

export function generateMetadata({ params }: { params: Promise<{ id: string }> }) {
  return { title: 'Application' }
}

export default async function AppDetailPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params
  const app = apps.find((a) => a.id === id)
  if (!app) notFound()

  const appDeployments = deployments.filter((d) => d.appId === id)

  return <AppDetailView app={app} deployments={appDeployments} />
}
