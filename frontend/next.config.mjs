// Set this to proxy /api and /openapi at the Next layer, which is what you want
// when running `next dev` against an API on another port. It is deliberately
// NOT set in the production image: there Caddy owns that routing and the
// request never reaches Node, so a rewrite would at best be dead weight and at
// worst point at a port inside the UI container where nothing is listening.
//
// Rewrites are baked in at build time (`output: 'standalone'` serialises this
// config into server.js), so setting it at runtime has no effect.
const devApiOrigin = process.env.AIRSIDE_API_URL

/** @type {import('next').NextConfig} */
const nextConfig = {
  // A self-contained server plus only the traced subset of node_modules, so the
  // runtime image needs no package install. The build does not copy `public` or
  // `.next/static` into it — the Dockerfile does that, per Next's own docs.
  output: 'standalone',

  // No optimisation pipeline, which also keeps `sharp` out of the image. This
  // dashboard has no photography in it; it has icons and a wordmark.
  images: { unoptimized: true },

  ...(devApiOrigin
    ? {
        async rewrites() {
          return [
            { source: '/api/:path*', destination: `${devApiOrigin}/api/:path*` },
            { source: '/openapi/:path*', destination: `${devApiOrigin}/openapi/:path*` },
          ]
        },
      }
    : {}),
}

export default nextConfig
