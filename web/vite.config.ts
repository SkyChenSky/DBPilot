import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// https://vite.dev/config/
export default defineConfig({
  plugins: [vue()],
  server: {
    port: 5173,
    proxy: {
      // 开发模式：/api 代理到后端 Host
      '/api': {
        target: 'http://localhost:5200',
        changeOrigin: true,
      },
    },
  },
  build: {
    // 构建产物直接输出到 DBPilot.AspNetCore 库的 wwwroot（内嵌进 nupkg）（.gitignore 已忽略该目录）
    outDir: '../src/DBPilot.AspNetCore/wwwroot',
    emptyOutDir: true,
  },
})
