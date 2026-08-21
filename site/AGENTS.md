# Public documentation site

`site/` is the published Astro 7 / Starlight documentation site for gatOS. It is served at
`https://meow.science.fail/gatOS/`, so every hand-written root-relative internal link must begin
with `/gatOS/` and normally end with `/`.

Before editing, read the repo-local `astro`, `starlight`, and (for guides) `tutorials` skills. API
facts come from code and the root contracts; never infer a path, action, enum, unit, range, gate, or
error from nearby prose.

## Content architecture

- `src/content/docs/intro/` — installation, mental model, interfaces, and feature/status orientation.
- `src/content/docs/guides/` — task-oriented lessons and project journeys. The landing page is
  `guides/index.mdx`; every substantial guide teaches both `/sim` and HTTP where the house style
  requires it.
- `src/content/docs/reference/` — player-readable projection of `SPEC_9P_FILESYSTEM.md` and config.
- `src/content/docs/mcp/` — agent operating manual plus per-tool/resource wrappers.
- `src/data/mcp-reference.ts` — curated MCP reference content used by every leaf page.
- `src/components/McpReference.astro` — renderer for that content. Multipurpose tools must keep each
  operation's exact call shape, accepted values, and complete example together.
- `astro.config.mjs` — curated sidebar. Leaf MCP pages stay reachable and searchable through the
  tool/resource directories; do not flatten all of them into primary navigation.

## Source-of-truth and lockstep rules

For `/sim`, HTTP, MQTT, serial, units, command actions, phases, and errno, start with
`SPEC_9P_FILESYSTEM.md` and the implementation. For MCP names, schemas, resources, envelopes, and
transport behavior, start with `gatOS.Mcp/`, its tests, and `SPEC_MCP.md`.

An MCP surface change updates, in the same work item:

1. the implementation and discovery/schema tests;
2. `SPEC_MCP.md`;
3. `src/data/mcp-reference.ts` and the affected `src/content/docs/mcp/**` narrative;
4. shared agent guidance (`.agents/skills/gatos/`) when usage behavior changes.

A `/sim` surface change follows the root AGENTS.md constitution and updates the affected public
reference/guide pages. A guide must never become a second exhaustive catalog.

## Commands

Run from `site/` with pnpm:

```sh
pnpm install
pnpm build
pnpm lint
pnpm format
pnpm astro dev --background
pnpm astro dev status
pnpm astro dev logs
pnpm astro dev stop
```

`pnpm build` is required before handoff. It catches MDX/frontmatter/component failures, but it does
not prove every manually written route exists; verify changed `/gatOS/...` links against the built
routes as part of review.
