import type { NextConfig } from 'next';

const nextConfig: NextConfig = {
  transpilePackages: ['@netwatch/shared'],
  experimental: {
    optimizePackageImports: ['lucide-react'],
  },
};

export default nextConfig;
