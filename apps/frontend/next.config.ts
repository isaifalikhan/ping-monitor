import type { NextConfig } from 'next';
import { SECURITY_HEADERS } from './src/lib/api/security-headers';

const securityHeaderEntries = Object.entries(SECURITY_HEADERS).map(([key, value]) => ({
  key,
  value,
}));

const nextConfig: NextConfig = {
  transpilePackages: ['@netwatch/shared'],
  experimental: {
    optimizePackageImports: ['lucide-react', 'recharts'],
    typedRoutes: false,
  },
  serverExternalPackages: ['bcryptjs', 'nodemailer'],
  typescript: {
    // Large API surface can exceed local RAM during `next build` typecheck.
    // Vercel builds typically succeed; run `tsc --noEmit` in CI with adequate memory.
    ignoreBuildErrors: true,
  },
  async headers() {
    return [
      {
        source: '/:path*',
        headers: securityHeaderEntries,
      },
    ];
  },
};

export default nextConfig;
