---
name: okf
description: "Author, revise, review, or organize Open Knowledge Format (OKF) v0.2 knowledge bundles: Markdown concept documents with YAML frontmatter, bundle navigation, provenance, trust, lifecycle, and Attested Computation contracts. Use whenever creating or maintaining an OKF corpus or checking it for OKF compliance."
---

# OKF Authoring

Create portable, human-readable knowledge bundles that conform to OKF v0.2. Treat the bundle as a directory tree: ordinary Markdown files are concepts; `index.md` and `log.md` are reserved navigation/history files.

## Core rules

Follow these rules for ordinary OKF authoring; load a reference only for an edge case.

### Concepts

- Make each non-reserved `.md` file UTF-8 Markdown starting with parseable YAML frontmatter delimited by `---`.
- Supply a non-empty, descriptive `type`; it is the sole universally required field. Unknown types are valid.
- Add `title`, one-sentence `description`, canonical `resource`, and short-string `tags` whenever they make the concept easier to discover or use.
- Write structural Markdown (headings, lists, tables, fenced code); `# Schema`, `# Examples`, and `# Computation` have conventional meanings but are never required.
- Preserve unknown frontmatter keys. Do not invent facts, provenance, verification, or freshness dates.

```markdown
---
type: API Endpoint
title: Get customer
description: Retrieves one customer by stable ID.
resource: https://api.example.test/customers/{id}
tags: [customers, api]
---

# Examples
```

### Provenance, trust, and lifecycle

- Use `sources` for material the concept derives from. Each source needs `resource`; assign a stable `id` when a body claim cites it.
- Attribute an individual claim with a Markdown footnote keyed to that ID: `Claim.[^policy]` and `[^policy]: Policy title`. Never use source-list positions as keys.
- Record authorship as `generated: { by, at }`; both values are required when that mapping exists and `at` is ISO 8601.
- Record verification as `verified: { by, at }` or a list of such mappings. Use actors as `<producer>/<version>`, `human:<id>`, or `process:<id>`; the `human:` prefix determines human-reviewed trust.
- Use `status: draft`, `stable`, or `deprecated` (omitted means stable), and an absolute `stale_after: YYYY-MM-DD` if an expiry boundary is known. Content is stale on or after that date.

```yaml
sources:
  - id: policy
    resource: https://example.test/policy
    title: Revenue policy
generated: { by: catalog_agent/1.4, at: 2026-06-20T22:53:05Z }
verified: { by: human:alex, at: 2026-06-25T09:00:00Z }
status: stable
```

### Bundle navigation and paths

- Reserve `index.md` for optional directory navigation and `log.md` for optional update history; neither is a concept. Only a bundle-root `index.md` may have `okf_version: "0.2"` frontmatter.
- Organize an index as heading-grouped Markdown list entries. Include each target’s description when possible.
- Organize a log newest-first beneath `YYYY-MM-DD` headings.
- Link concepts with standard Markdown. Prefer bundle-relative `/path/to/concept.md` links for relocation stability; relative links are also valid. A broken link remains conformant.
- Use an absolute URL, a bundle-relative path, or a relative path in path-valued fields such as `resource`, `sources[].resource`, `computation`, `executor.resource`, and `attester.resource`.

### Attested Computations

For `type: Attested Computation`, set required `runtime`, declare the typed `parameters`, and supply `executor` run instructions plus receipt fields and a deterministic (no-LLM) `attester`. Store the sanctioned computation in exactly one place: a fenced block under `# Computation`, or an external `computation` path.

Only provide values for declared parameters. Never edit, replace, or improvise the sanctioned computation. Keep a computation as its own concept and link to it from metrics or narrative concepts. `verified` reviews the definition; runtime attestation validates one run—do not conflate them.

## Authoring workflow

1. Inspect the target directory and its nearest `index.md` before adding or relocating a concept.
2. Create or revise the concept using the core rules above; update direct relationships as Markdown links.
3. Add optional provenance, trust, and lifecycle facts only when they are known and evidence-backed.
4. Update the relevant `index.md` and `log.md` if present and affected.
5. Validate every concept has parseable frontmatter and `type`; then validate special computation and source-footnote rules when used.

## Reference map

Load only the material needed for the task:

- [Concept documents and conformance](references/concepts-and-conformance.md) — required frontmatter, body conventions, extensions, and a preflight checklist.
- [Provenance, trust, and lifecycle](references/provenance-trust-lifecycle.md) — `sources`, claim attribution, actors, verification, status, and freshness.
- [Paths and bundle navigation](references/paths-and-navigation.md) — links, path-valued fields, `references/`, `index.md`, and `log.md`.
- [Attested Computations](references/attested-computations.md) — special computation concepts, executor/attester contracts, and review rules.
- [Versioning and migration](references/versioning-and-migration.md) — version declaration and v0.1 compatibility.

## Guardrails

- Keep `index.md` and `log.md` free of concept semantics. Only a bundle-root `index.md` may have `okf_version: "0.2"` frontmatter.
- Do not reject a corpus merely for unknown types, unknown frontmatter keys, absent optional metadata, missing indexes, or broken links.
- Do not use legacy `timestamp` or a `# Citations` list for new v0.2 content; write `generated.at` and `sources` instead. Preserve legacy content unless the task calls for migration.
- For an Attested Computation, allow users to supply values only for declared parameters. Do not rewrite its sanctioned computation.
