# gatOS public documentation

This directory builds the public Astro/Starlight site at
<https://meow.science.fail/gatOS/>. Player-facing content lives under `src/content/docs/`; the
curated sidebar is in `astro.config.mjs`.

```sh
pnpm install
pnpm build
pnpm lint
pnpm astro dev --background
```

Use `pnpm astro dev status|logs|stop` to manage the background development server.

The root `SPEC_9P_FILESYSTEM.md` is the exhaustive `/sim` and transport contract.
`SPEC_MCP.md` plus `gatOS.Mcp/` define the MCP surface. MCP leaf pages render curated data from
`src/data/mcp-reference.ts` through `src/components/McpReference.astro`; keep each multipurpose
operation's legal call shape and example adjacent.

See [AGENTS.md](./AGENTS.md) for the authoring, link-prefix, lockstep, and validation rules.
