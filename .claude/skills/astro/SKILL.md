---
name: astro
description: >-
  Reference for Astro — the core web framework the gatOS docs site (under site/) is built on. Use
  this when authoring or editing pages in site/, working with .astro/.md/.mdx files, content
  collections, the astro.config.mjs, or component/JSX-in-Markdown mechanics. Covers Astro component
  anatomy, using components in MDX, content collections, frontmatter, imports, expressions, and the
  build/dev commands — enough to work the site without leaving the repo. Pair with the `starlight`
  skill (the docs theme + its components) and the `tutorials` skill (the gatOS house style).
---

# Astro — the core framework under `site/`

The gatOS docs site is **Astro 7 + Starlight 0.41**. Astro is the underlying framework; **Starlight**
is a documentation theme built on it (its own [`starlight`](../starlight/SKILL.md) skill). This skill
covers the Astro layer you touch when authoring docs: component files, using components inside
Markdown/MDX, content collections, and the config. You rarely need raw `.astro` authoring for a
tutorial — most work is MDX pages that *use* Starlight components — but this is the model underneath.

**When to reach for this vs the `starlight` skill:** Starlight for "which component / what
frontmatter field / how do asides work"; Astro for "how does MDX import a component", "what's a
content collection", "how do I make a small `.astro` component", "what does `astro.config.mjs` do".

## The mental model

- Astro renders **to static HTML at build time**, shipping **zero JavaScript by default**. A page is
  fast because nothing hydrates unless you opt in (Astro Islands — not needed for docs prose).
- **Three content file types** live under `src/content/docs/` (Starlight's collection):
  - `.md` — plain Markdown + YAML frontmatter. Content only, no components.
  - `.mdx` — Markdown **plus** JSX: you can `import` and render components inline. **Default to this
    for gatOS tutorials** — asides work in both, but `<Tabs>`, `<Steps>`, `<FileTree>`, `<Card>`
    need `.mdx`.
  - `.astro` — a full Astro component (script + template). You author these only when building a
    reusable UI piece; a tutorial page is `.mdx`, not `.astro`.

## Anatomy of an `.astro` component

Two parts, split by a `---` code fence:

```astro
---
// Component script — runs at BUILD time, server-side only. Never ships to the browser.
import SomeComponent from './SomeComponent.astro';
interface Props { title: string; greeting?: string; }
const { title, greeting = 'Hello' } = Astro.props;   // props come in via Astro.props
const items = ['one', 'two', 'three'];
---
<!-- Component template — HTML with {JavaScript expressions} and components -->
<h1>{greeting}, {title}!</h1>
<ul>
  {items.map((item) => <li>{item}</li>)}
</ul>
<SomeComponent />
```

- **Props**: declare a `Props` interface for type safety; destructure from `Astro.props`; default
  values with `= …`.
- **Expressions**: `{ }` in the template evaluates JS — interpolation, `.map()` for lists, ternaries
  for conditionals (`{cond && <p>…</p>}`).
- **Slots** (children): `<slot />` renders default children; `<slot name="header" />` renders content
  marked `<Foo><h1 slot="header">…</h1></Foo>`. A `<slot>` can hold fallback content when empty.
- The custom SVG in `reference-frames.mdx` is inline HTML in an `.mdx` file — you don't need an
  `.astro` component for a one-off diagram; only build one if it's reused.

## Using components inside MDX (the pattern tutorials actually use)

A tutorial page is `.mdx`. Import components at the top, then render them as JSX tags (capitalized):

```mdx
---
title: My tutorial
---
import { Tabs, TabItem, Steps, Aside } from '@astrojs/starlight/components';

Prose in Markdown, **bold**, `code`, and links all work as normal.

<Aside type="tip" title="Heads up">You can mix components and Markdown freely.</Aside>

<Steps>
1. First do this.
2. Then this.
</Steps>
```

Rules that bite:
- **Component names must be capitalized** (`<Tabs>`, not `<tabs>`) — lowercase is treated as an HTML
  tag.
- **Import before use**, from `@astrojs/starlight/components` for Starlight's built-ins (see the
  `starlight` skill) or a relative path for your own `.astro`.
- **JSX expressions** work in MDX: `export const x = 2;` then `The answer is {x}.` Frontmatter values
  are on the `frontmatter` object (`{frontmatter.title}`).
- **Blank lines matter** around block components. Keep a blank line between a component tag and
  adjacent Markdown so the Markdown parses (especially inside `<TabItem>`/`<Steps>`).
- MDX comments are `{/* … */}`, not `<!-- -->`.

## Content collections (how the docs pages are wired)

Starlight registers a `docs` **content collection**. The site's `src/content.config.ts`:

```ts
import { defineCollection } from "astro:content";
import { docsLoader } from "@astrojs/starlight/loaders";
import { docsSchema } from "@astrojs/starlight/schema";

export const collections = {
  docs: defineCollection({ loader: docsLoader(), schema: docsSchema() }),
};
```

What this means for an author:
- Every file under `src/content/docs/**` is a collection **entry**; its path becomes its route
  (`docs/guides/foo.mdx` → `/guides/foo/`, under the site `base` — see below).
- `docsSchema()` **validates the frontmatter** at build time. An unknown or mistyped field fails the
  build — this is a feature: it catches typos in `sidebar.order`, `badge.variant`, etc. The field
  list is the Starlight frontmatter reference (in the `starlight` skill).
- You do **not** edit `content.config.ts` to add a tutorial — just drop the `.mdx` in `guides/`.

## The config: `astro.config.mjs`

```js
import { defineConfig } from "astro/config";
import starlight from "@astrojs/starlight";

export default defineConfig({
  site: "https://meow.science.fail",   // absolute origin for canonical/OG URLs
  base: "/gatOS/",                       // path prefix — the site lives at /gatOS/
  integrations: [ starlight({ /* title, sidebar, social, editLink … */ }) ],
});
```

- **`site` + `base` are why internal links carry the `/gatOS/` prefix.** A root-relative link in a
  page must include it: `/gatOS/guides/point-at-parent/`, not `/guides/point-at-parent/`. (Starlight's
  own components handle this for you; hand-written `<a href>`/Markdown links must include the base.
  The existing pages link `/gatOS/guides/…` — match that.)
- The **sidebar is autogenerated** from the `guides/` and `reference/` directories, so a new file
  appears with no config change; order/label come from each page's frontmatter.
- Integrations (like `starlight`) are configured here — you rarely touch this for content work.

## Assets & images

- Local images live in `site/src/assets/` and are referenced with a **relative** path from the
  `.mdx` (`![alt](../../../assets/frames/frames_cci.png)`). Astro optimizes them at build.
- Prefer `.webp`/`.png`; give every image real alt text (the frames page is the model).
- Inline SVG is fine directly in `.mdx` for a one-off diagram (see `reference-frames.mdx`), and it
  can use `currentColor` to adapt to light/dark themes.

## Build & dev (run from `site/`, the repo uses **pnpm**)

```sh
pnpm install                 # once
pnpm astro dev --background  # dev server (background mode — see site/CLAUDE.md)
pnpm astro dev status        # is it up?
pnpm astro dev logs          # tail output
pnpm astro dev stop          # shut down
pnpm build                   # production build — FAILS on bad MDX and broken internal links
pnpm preview                 # serve the built site
```

**Always `pnpm build` before calling a page done** — it's the cheapest check for an unclosed `<Tabs>`,
a mistyped frontmatter field (schema validation), or a dead internal link. `oxlint`/`oxfmt` are wired
via `pnpm lint` / `pnpm format`.

## Gotchas

- **Base prefix on links** — forget `/gatOS/` and the link 404s only in production (dev may be
  lenient). Always include it in hand-written links.
- **`.md` can't use components** — if a page needs `<Tabs>`/`<FileTree>`/`<Steps>`, it must be `.mdx`.
- **Frontmatter is schema-checked** — a build error like "Invalid frontmatter" means a bad field
  name/type, not broken prose. Check against the Starlight frontmatter reference.
- **Capitalized component tags + imports** — the two most common MDX mistakes.
- Astro version here is **7.0.6**; Starlight **0.41.2** (see `site/package.json`). Match doc examples
  to these; newer component props may not exist yet.

## Where to look next

- Components, asides, frontmatter fields, code blocks → the **[`starlight`](../starlight/SKILL.md)**
  skill (the theme layer).
- The gatOS tutorial house style, ladder, and dual-transport convention → the
  **[`tutorials`](../tutorials/SKILL.md)** skill.
- Deeper Astro topics not covered here (Islands/hydration, framework components, middleware, dynamic
  routes) → `https://docs.astro.build`. Docs work for gatOS almost never needs them.
