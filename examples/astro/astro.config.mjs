import { defineConfig } from 'astro/config';
import { resolve } from 'node:path';
import { unified } from '@astrojs/markdown-remark';
import rehypeSanitize from 'rehype-sanitize';

const destination = process.env.TATAMI_OUTPUT_DIR;
if (!destination || !process.env.TATAMI_CONTENT_DIR) {
  throw new Error('Tatamiの外部ビルダーから起動してください（TATAMI_CONTENT_DIR / TATAMI_OUTPUT_DIR が必要です）。');
}
export default defineConfig({
  output: 'static',
  outDir: resolve(destination),
  build: { inlineStylesheets: 'never' },
  markdown: { processor: unified({ rehypePlugins: [rehypeSanitize] }) },
});
