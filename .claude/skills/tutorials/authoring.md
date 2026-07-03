# Authoring mechanics — Starlight / MDX for the gatOS docs site

Everything you need to turn a lesson into a rendered page. The site is **Astro + Starlight 0.41.2**
(`site/`); tutorials are MDX files that Starlight themes and sidebars automatically.

---

## 1. Where a tutorial lives, and its URL

- Put the file at **`site/src/content/docs/guides/<slug>.mdx`**.
- Its URL is **`/guides/<slug>/`** (the path under `docs/` becomes the route).
- Use `.mdx` (not `.md`) whenever you need components — asides work in both, but `<Tabs>`, `<Steps>`,
  `<Card>` etc. require `.mdx`. Default to `.mdx` for tutorials.
- Slugs are kebab-case and stable (they're the permalink). The existing model page is
  `guides/vessel-control-point-at-parent.mdx`.

The sidebar is **auto-generated** from the `guides/` directory (`site/astro.config.mjs`:
`autogenerate: { directory: 'guides' }`), so a new file shows up with no config edit. Order and label
are controlled per-file in frontmatter (§2).

---

## 2. Frontmatter (the `docsSchema`)

```yaml
---
title: Vessel — Point at Prograde          # required; the H1 and sidebar/tab label
description: >-                             # one sentence; SEO + social + the page subtitle
  Hold a vessel's nose on its prograde vector with the onboard flight computer, over /sim and HTTP.
sidebar:
  order: 3                                  # position within Guides (lower = higher). Omit → alphabetical
  label: Point at prograde                  # optional shorter sidebar text (defaults to title)
  badge: { text: New, variant: tip }        # optional pill: tip|note|danger|caution|success|default
tableOfContents:                            # optional; default shows H2+H3
  minHeadingLevel: 2
  maxHeadingLevel: 3
---
```

Only `title` is required. Always write a concrete, outcome-named `description` — it becomes the page
subtitle and the search/social snippet. Use `sidebar.order` to slot a tutorial into the ladder
(`curriculum.md`) rather than relying on alphabetical order.

---

## 3. Components you'll use (import at the top of the `.mdx`)

```mdx
import { Tabs, TabItem, Steps, Aside, Card, CardGrid, LinkCard, FileTree, Badge } from '@astrojs/starlight/components';
```

### Asides (the tutorial's margin notes)

Markdown shorthand (preferred — works in `.md` and `.mdx`, no import):

```md
:::note
Plain heads-up. Default title "Note".
:::

:::tip[Shortcut: let the autopilot name it]
Custom title in the brackets. Use for the "you can skip the math" shortcut.
:::

:::caution[Solver-phase latency]
Footguns: authority gate, time-warp, `controllable == 0`, one-shot already fired.
:::

:::danger
Irreversible / destructive (rare in flight tutorials).
:::
```

Component form (identical output, when you want it inside other JSX): `<Aside type="tip" title="…">…</Aside>`.

### Tabs — the dual-transport presentation (**the key pattern**)

Show the `/sim` file way and the HTTP way side by side. Give **every** transport Tabs group the same
`syncKey` so a reader picks once and the whole page (and future visits) follow:

```mdx
<Tabs syncKey="transport">
  <TabItem label="/sim (in guest)">
    ```sh
    echo Prograde > /sim/vessels/active/ctl/attitude_mode
    ```
  </TabItem>
  <TabItem label="HTTP (host)">
    ```sh
    curl -X POST --data 'Prograde' \
      http://127.0.0.1:4242/v1/fs/vessels/active/ctl/attitude_mode
    ```
  </TabItem>
</Tabs>
```

Keep the two tabs logically identical — same steps, same values — differing only at the I/O edge. Pull
the exact commands from [`docs/TUTORIAL_DATA_REFERENCE.md §2`](../../../docs/TUTORIAL_DATA_REFERENCE.md).
For a program shown in one language, you can instead use `syncKey="lang"` tabs (Python / TypeScript /
shell) — but the transport split is the one every actuating tutorial must have.

### Steps — numbered procedures

Wrap an ordered list; Starlight renders connected step markers:

```mdx
<Steps>

1. Read the vessel's position in CCI.
2. Negate it to get the aim direction.
3. Write the setpoint and exit.

</Steps>
```

### Code blocks (Expressive Code)

Fenced blocks accept a meta string for title + emphasis:

````md
```python title="point-at-parent.py" {14,18-20}
pos = read_vec(f"{base}/position/cci")   # highlighted line 14
aim = [-c for c in pos]                   # lines 18-20 highlighted
```
````

- `title="…"` renders a file/tab caption. `{n}` / `{a-b}` highlight lines. `"text"` marks a substring;
  `ins={…}` / `del={…}` show diff add/remove. `sh`/`bash`/`ansi` render a terminal frame automatically.
- For code assembled in JSX, the `<Code code={str} lang="python" title="…" />` component works too.

### Cards & links

```mdx
<CardGrid>
  <Card title="Next: schedule a burn" icon="rocket">Turn the pointing into a maneuver.</Card>
</CardGrid>

<LinkCard title="Point at parent" href="/guides/vessel-control-point-at-parent/"
  description="The gentler first program this one builds on." />
```

Use a `<LinkCard>` (or a plain link) at the foot of each tutorial to the **next rung**.

---

## 4. Linking (published pages ≠ repo files)

A rendered `.mdx` can only link to things the *reader* can reach:

- **Another tutorial / site page:** root-relative, `[Point at parent](/guides/vessel-control-point-at-parent/)`.
  (Include the trailing slash.)
- **The canonical API catalog:** the SPEC on GitHub —
  `https://github.com/meow-sci/gatOS/blob/main/SPEC_9P_FILESYSTEM.md`. Do **not** link `../../../SPEC…`
  from a published page; that path doesn't exist on the site.
- The repo-relative links inside *this skill and `docs/TUTORIAL_DATA_REFERENCE.md`* are for **you, the
  author**, not for the tutorial body.

If a tutorial needs a fact from the SPEC, **inline the fact** (with the value/units) and link the SPEC
on GitHub for the full catalog — don't send the reader to a repo file.

---

## 5. Run the site to verify (do this before finishing)

From `site/` (the repo uses **pnpm**):

```sh
cd site
pnpm install            # once
pnpm dev                # or: pnpm astro dev  — serves http://localhost:4321
pnpm build              # production build; catches broken internal links & MDX errors
pnpm preview            # serve the built site
```

Per `site/AGENTS.md`, prefer **background mode** so the server doesn't block the session:

```sh
pnpm astro dev --background
pnpm astro dev status   # is it up?
pnpm astro dev logs     # tail output
pnpm astro dev stop     # shut down
```

Always `pnpm build` a new/edited tutorial before calling it done — the build fails on malformed MDX
and (with the link checker) on broken internal links, which is the cheapest way to catch a typo'd
route or an unclosed `<Tabs>`.

---

## 6. Assets

Images go in `site/src/assets/` and are referenced relatively from the `.mdx`
(`![alt](../../assets/foo.webp)`) or, for the hero, via the frontmatter `image.file` path (see
`index.mdx`). Prefer `.webp`. Keep tutorial screenshots small; most flight tutorials need none — the
in-game result *is* the payoff, described in the "Run it" section.

---

## 7. Checklist for a finished tutorial

- [ ] Frontmatter: `title` + outcome-named `description` (+ `sidebar.order` slotting it in the ladder).
- [ ] Opens with the promise + "the sim is a filesystem" frame, then **"the idea in one picture"**.
- [ ] One complete, heavily-commented program using the canonical `snippets.md` helpers.
- [ ] **Both transports** shown via synced `<Tabs syncKey="transport">`.
- [ ] A "Run it" section with the exact command and the in-game result to look for.
- [ ] Asides for the caveat / shortcut / footgun; every path & route checked against the SPEC.
- [ ] Links back to the prior rung and forward to the next.
- [ ] `pnpm build` passes.
