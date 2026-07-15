import react from '@vitejs/plugin-react';
import { defineConfig, loadEnv } from 'vite';

export default defineConfig(({ mode }) => {
  // VITE_API_URL 允许多个验证实例使用不同后端端口；未配置时回退到默认本地 .NET 服务。
  const env = loadEnv(mode, process.cwd(), '');

  return {
    plugins: react(),
    server: {
      host: '0.0.0.0',
      port: 5174,
      proxy: {
        // 浏览器始终请求同源 /api，由 Vite 代理到后端，开发环境无需额外放宽 CORS。
        '/api': {
          target: env.VITE_API_URL || 'http://localhost:5006',
          changeOrigin: true,
        },
      },
    },
    build: {
      rollupOptions: {
        output: {
          manualChunks(moduleId) {
            // Univer 体积较大且变化频率低，拆为独立 chunk 便于生产环境长期缓存和单独观察包体积。
            return moduleId.includes('@univerjs') ? 'univer' : undefined;
          },
        },
      },
    },
  };
});
