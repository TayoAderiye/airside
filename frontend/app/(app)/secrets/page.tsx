import { secrets } from '@/lib/api/mock'
import { SecretsView } from '@/components/secrets/secrets-view'

export const metadata = { title: 'Secrets' }

export default function SecretsPage() {
  return <SecretsView secrets={secrets} />
}
