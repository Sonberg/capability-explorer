import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { LikeC4VitePlugin } from 'likec4/vite-plugin';
export default defineConfig({ plugins: [react(), LikeC4VitePlugin({ workspace: './src/model', throwIfInvalid: true })], preview: { allowedHosts: ['host.docker.internal'] }, build: { sourcemap: false } });
