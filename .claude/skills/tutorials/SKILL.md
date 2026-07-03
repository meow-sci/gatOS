---
name: tutorials
description: >-
  Author tutorial-style documentation for the gatOS Astro/Starlight docs site (under site/) — the
  progressive `guides/` series that teaches writing flight-computer / autopilot programs against the
  gatOS /sim filesystem and its HTTP /v1 mirror. Use this when asked to write, add, or revise a gatOS
  tutorial or guide, plan the tutorial curriculum, or turn a /sim feature into a lesson. Covers the
  house style, Starlight/MDX mechanics, the dual-transport (/sim file + HTTP) presentation convention,
  the reusable code-snippet library, and the beginner→advanced tutorial ladder. Pairs with the `gatos`
  skill (how the sim works) and docs/TUTORIAL_DATA_REFERENCE.md (the data tutorials are built from).
---

# Writing gatOS tutorials

This skill makes authoring a tutorial for the gatOS docs site **trivial**: it supplies the house
style, the site mechanics, the dual-transport convention, a reusable snippet library, and a
beginner→advanced curriculum, so a session can go straight from "write a tutorial about X" to a
polished `.mdx` page. It does **not** teach the sim — that's the `gatos` skill — it teaches how to
*write the sim up as a lesson*.

## The three sources you author from

| Source | What it gives you | Path |
|---|---|---|
| **`docs/TUTORIAL_DATA_REFERENCE.md`** | the `/sim` data + file↔HTTP correspondence tutorials are built from (frames, flight-computer controls, telemetry, pacing, gating) | [`../../../docs/TUTORIAL_DATA_REFERENCE.md`](../../../docs/TUTORIAL_DATA_REFERENCE.md) |
| **the `gatos` skill** | how the sim actually works — the authoritative catalog + frame math + worked programs | [`../gatos/SKILL.md`](../gatos/SKILL.md), [`coordinate-frames.md`](../gatos/coordinate-frames.md), [`recipes.md`](../gatos/recipes.md), [`SPEC`](../../../SPEC_9P_FILESYSTEM.md) |
| **this skill's sidecars** | how to write it up | `authoring.md`, `curriculum.md`, `snippets.md` (below) |

## Read these as needed

| Sidecar | When |
|---|---|
| [`authoring.md`](authoring.md) | **Starlight/MDX mechanics**: frontmatter, file placement, sidebar, the components (asides, `<Tabs>`, `<Steps>`, `<Code>`, `<Card>`), the synced dual-transport tabs pattern, linking, the dev server, build/preview |
| [`curriculum.md`](curriculum.md) | **the tutorial ladder** — the ordered beginner→advanced series, each entry's goal, prerequisites, `/sim` surface, and gotchas. Consult before writing *any* tutorial so it fits the progression |
| [`snippets.md`](snippets.md) | **the reusable helper library** every tutorial copies — connection/transport-select, read/write, the verbatim Body→CCI quaternion, vector math, gating, sim-time pacing — in Python (in-guest), Bun/TS (host), and shell |

Where tutorials live: **`site/src/content/docs/guides/*.mdx`**. Starlight auto-generates the sidebar
from the directory (`site/astro.config.mjs`), so a new file appears automatically. The one existing
work-in-progress model page is
[`vessel-control-point-at-parent.mdx`](../../../site/src/content/docs/guides/vessel-control-point-at-parent.mdx)
— read it before writing; it *is* the house style.

## The house style — anatomy of a gatOS tutorial

Every tutorial follows the same rhythm (proven by the model page). A future author should reproduce it:

1. **Frontmatter** — `title` + a one-sentence `description` that names the concrete outcome. (See
   `authoring.md`.)
2. **One-paragraph promise** — what the reader will make happen, in plain language, and that it's all
   just file I/O on `/sim` (no SDK, no RPC). Set the "the simulation is a filesystem" frame early.
3. **"The idea in one picture"** — the *concept* before the code. Ground the physics/frame fact the
   program rests on (e.g. "CCI origin is the body's center, so aim = `-position/cci`"). This is what
   makes a gatOS tutorial teach rather than transcribe. Pull the fact from
   `docs/TUTORIAL_DATA_REFERENCE.md` / `coordinate-frames.md`; don't re-derive it.
4. **The program** — one complete, copy-pasteable, **heavily-commented** program. Comments explain the
   *why* (frame choice, phase latency, why negate the vector), not the syntax. Reuse the canonical
   helpers from `snippets.md` verbatim so every tutorial's plumbing is identical and correct.
5. **Both transports** — show the `/sim` file way **and** the HTTP `/v1` way (the deliberate
   requirement). Use the synced `<Tabs>` pattern from `authoring.md` so the reader picks a transport
   once and the whole page follows.
6. **"Run it"** — the exact command(s) and what the reader should *see* happen in-game.
7. **Asides for the edges** — a `:::note` for a caveat (e.g. "this is a one-shot setpoint; it goes
   stale"), a `:::tip` for a shortcut (e.g. "the named mode does this for you"), a `:::caution` for a
   footgun (authority gate, warp, `controllable==0`). Every non-trivial tutorial ends by pointing at
   the next rung of the ladder.

Tone: precise, warm, second-person, short sentences. Explain one new idea per tutorial; lean on
earlier rungs for the rest (link back). Prefer the clearest demonstration over the most efficient
code early, then upgrade (scalar files → atomic `telemetry` doc; hand-rolled loop → gated loop).

## The dual-transport rule (non-negotiable)

Tutorials teach **two ways to reach the same API** so a reader can use whichever suits them:

- **In the guest** — `cat`/`echo` on `/sim/…` (the filesystem *is* the API).
- **On the host** — HTTP `/v1` (`GET /v1/fs/<path>`, `POST /v1/command` or `POST /v1/fs/<path>`).

The file↔HTTP correspondence for every operation is the money table in
[`docs/TUTORIAL_DATA_REFERENCE.md §2`](../../../docs/TUTORIAL_DATA_REFERENCE.md) — copy the row.
Present the two with synced `<Tabs syncKey="transport">` (see `authoring.md`) so the choice is made
once per page. Keep the *logic* identical across tabs; only the I/O edge differs.

## Workflow for "write a tutorial about X"

1. **Locate X on the ladder** (`curriculum.md`) — confirm its prerequisites exist (or note them) so
   it slots into the progression, and lift its goal / `/sim` surface / gotchas.
2. **Gather the data facts** from `docs/TUTORIAL_DATA_REFERENCE.md` (and the SPEC/`gatos` skill for
   depth) — the exact paths, frames, units, phase, and the file↔HTTP rows.
3. **Draft the `.mdx`** in `site/src/content/docs/guides/` following the house-style rhythm, reusing
   `snippets.md` helpers and the synced-tabs dual-transport pattern.
4. **Verify it renders** — run the dev server (`authoring.md`) and check the page; confirm every
   `/sim` path and HTTP route matches the SPEC (a tutorial that teaches a wrong path is worse than
   none).
5. **Wire the progression** — link back to the prior rung and forward to the next; if you introduced
   a genuinely new helper, add it to `snippets.md` so later tutorials reuse it.

## Guardrails

- **Accuracy beats prose.** Every path, action key, unit, and phase must match the SPEC. When unsure,
  read [`SPEC_9P_FILESYSTEM.md`](../../../SPEC_9P_FILESYSTEM.md) — do not invent a path.
- **Don't duplicate the catalog** into a tutorial. Teach the slice, link the rest.
- **Published pages can't relative-link into the repo.** A `.mdx` in `site/` links to *other site
  pages* or to canonical URLs (the SPEC on GitHub: `https://github.com/meow-sci/gatOS/blob/main/SPEC_9P_FILESYSTEM.md`),
  never to `../../../docs/…`. The repo-relative links in *this skill* are for you, the author.
- **Keep the ladder coherent.** A tutorial that assumes an idea the reader hasn't met yet is a bug —
  either link its prerequisite rung or teach the idea inline.
- When you change how tutorials are written or add a rung, update `curriculum.md` / this file so the
  next session inherits it.
