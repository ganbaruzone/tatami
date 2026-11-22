import { defineCollection, z } from 'astro:content';
import { glob } from 'astro/loaders';

export const collections = {
  notes: defineCollection({
    loader: glob({ pattern: '*.md', base: process.env.TATAMI_CONTENT_DIR! }),
    schema: z.object({
      title: z.string().optional(),
      publish: z.literal(true),
      slug: z.string().optional(),
      date: z.coerce.date().optional(),
      tags: z.array(z.string()).optional(),
    }),
  }),
};
