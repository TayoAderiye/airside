import { PageHeader } from '@/components/ui/panel'
import { BackLink } from '@/components/ui/back-link'
import { DatabaseCreateForm } from '@/components/databases/create-form'

export const metadata = { title: 'New database' }

export default function NewDatabasePage() {
  return (
    <div className="flex flex-col gap-6">
      <BackLink href="/databases">Databases</BackLink>
      <PageHeader
        title="New database"
        description="Fields adapt to the engine you pick. Everything reserved here counts against host capacity immediately."
      />
      <DatabaseCreateForm />
    </div>
  )
}
