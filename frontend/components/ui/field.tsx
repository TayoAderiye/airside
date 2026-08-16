import { cn } from '@/lib/utils'

export function Field({
  label,
  htmlFor,
  hint,
  error,
  required,
  children,
  className,
}: {
  label?: string
  htmlFor?: string
  hint?: React.ReactNode
  error?: React.ReactNode
  required?: boolean
  children: React.ReactNode
  className?: string
}) {
  return (
    <div className={cn('flex flex-col gap-1.5', className)}>
      {label && (
        <label htmlFor={htmlFor} className="flex items-center gap-1 text-sm font-medium text-foreground">
          {label}
          {required && (
            <span className="text-failed" aria-hidden>
              *
            </span>
          )}
        </label>
      )}
      {children}
      {error ? (
        <p className="text-xs text-failed">{error}</p>
      ) : (
        hint && <p className="text-xs text-muted-foreground">{hint}</p>
      )}
    </div>
  )
}

export function Hint({ tone = 'muted', children }: { tone?: 'muted' | 'warn'; children: React.ReactNode }) {
  return (
    <p className={cn('mt-1 text-xs', tone === 'warn' ? 'text-degraded' : 'text-muted-foreground')}>{children}</p>
  )
}

const controlBase =
  'w-full rounded-md border border-input bg-background px-3 py-1.5 text-sm text-foreground placeholder:text-muted-foreground transition-colors focus-visible:border-ring disabled:cursor-not-allowed disabled:opacity-50'

export function TextInput({
  className,
  mono,
  ...props
}: React.InputHTMLAttributes<HTMLInputElement> & { mono?: boolean }) {
  return (
    <input
      className={cn(controlBase, mono && 'font-mono', className)}
      {...props}
    />
  )
}

export function Select({
  className,
  children,
  ...props
}: React.SelectHTMLAttributes<HTMLSelectElement>) {
  return (
    <div className="relative">
      <select className={cn(controlBase, 'appearance-none pr-8', className)} {...props}>
        {children}
      </select>
      <svg
        className="pointer-events-none absolute right-2.5 top-1/2 size-4 -translate-y-1/2 text-muted-foreground"
        viewBox="0 0 16 16"
        fill="none"
        aria-hidden
      >
        <path d="M4 6l4 4 4-4" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" />
      </svg>
    </div>
  )
}

/** Alias — same native select, named for intent at call sites. */
export const NativeSelect = Select

export function Segmented<T extends string>({
  value,
  onChange,
  options,
  id,
}: {
  value: T
  onChange: (v: T) => void
  options: { value: T; label: string }[]
  id?: string
}) {
  return (
    <div id={id} role="radiogroup" className="inline-flex rounded-md border border-input bg-secondary p-0.5">
      {options.map((o) => (
        <button
          key={o.value}
          type="button"
          role="radio"
          aria-checked={value === o.value}
          onClick={() => onChange(o.value)}
          className={cn(
            'rounded px-3 py-1 text-sm transition-colors',
            value === o.value ? 'bg-card text-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground',
          )}
        >
          {o.label}
        </button>
      ))}
    </div>
  )
}

export function Slider({
  value,
  onChange,
  min,
  max,
  step,
  id,
}: {
  value: number
  onChange: (v: number) => void
  min: number
  max: number
  step: number
  id?: string
}) {
  return (
    <input
      id={id}
      type="range"
      min={min}
      max={max}
      step={step}
      value={value}
      onChange={(e) => onChange(Number(e.target.value))}
      className="control-slider my-2 h-1.5 w-full cursor-pointer appearance-none rounded-full bg-secondary accent-primary"
    />
  )
}

export function Textarea({
  className,
  ...props
}: React.TextareaHTMLAttributes<HTMLTextAreaElement>) {
  return <textarea className={cn(controlBase, 'font-mono', className)} {...props} />
}

export function Toggle({
  checked,
  onChange,
  label,
  description,
  id,
}: {
  checked: boolean
  onChange: (v: boolean) => void
  label: string
  description?: string
  id?: string
}) {
  const control = (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={description ? label : undefined}
      id={id}
      onClick={() => onChange(!checked)}
      className={cn(
        'relative inline-flex h-5 w-9 shrink-0 items-center rounded-full border transition-colors',
        checked ? 'border-primary bg-primary/80' : 'border-input bg-secondary',
      )}
    >
      {!description && <span className="sr-only">{label}</span>}
      <span
        className={cn(
          'inline-block size-3.5 rounded-full bg-background transition-transform',
          checked ? 'translate-x-4' : 'translate-x-0.5',
        )}
      />
    </button>
  )

  if (!description) return control

  return (
    <div className="flex items-start justify-between gap-4 rounded-md border border-border bg-card/50 p-3">
      <div className="min-w-0">
        <p className="text-sm font-medium text-foreground">{label}</p>
        <p className="text-xs text-muted-foreground">{description}</p>
      </div>
      {control}
    </div>
  )
}
