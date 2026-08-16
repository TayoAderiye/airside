import type { Metadata, Viewport } from 'next'
import { Inter, JetBrains_Mono, Space_Grotesk } from 'next/font/google'
import { PRODUCT_NAME, PRODUCT_TAGLINE } from '@/lib/brand'
import { SessionProvider } from '@/lib/session'
import './globals.css'

const inter = Inter({
  subsets: ['latin'],
  variable: '--font-inter',
  display: 'swap',
})

const spaceGrotesk = Space_Grotesk({
  subsets: ['latin'],
  variable: '--font-space-grotesk',
  display: 'swap',
})

const jetbrainsMono = JetBrains_Mono({
  subsets: ['latin'],
  variable: '--font-jetbrains-mono',
  display: 'swap',
})

export const metadata: Metadata = {
  title: {
    default: PRODUCT_NAME,
    template: `%s · ${PRODUCT_NAME}`,
  },
  description: PRODUCT_TAGLINE,
}

export const viewport: Viewport = {
  colorScheme: 'dark',
  themeColor: '#0e1116',
}

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode
}>) {
  return (
    <html
      lang="en"
      className={`dark bg-background ${inter.variable} ${spaceGrotesk.variable} ${jetbrainsMono.variable}`}
    >
      <body className="font-sans antialiased">
        {/*
          No analytics, deliberately. This dashboard runs on someone else's
          server and is the console for their whole machine; a third-party
          beacon firing from an administrator's browser is not something a
          self-hosted tool gets to do quietly.
        */}
        <SessionProvider>{children}</SessionProvider>
      </body>
    </html>
  )
}
