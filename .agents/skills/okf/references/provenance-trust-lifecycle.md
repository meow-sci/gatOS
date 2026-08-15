# Provenance, trust, and lifecycle

## Contents

- [Sources and claims](#sources-and-claims)
- [Generation and verification](#generation-and-verification)
- [Lifecycle](#lifecycle)
- [Actors](#actors)
- Related: [concepts and conformance](concepts-and-conformance.md), [paths and navigation](paths-and-navigation.md), [Attested Computations](attested-computations.md)

All these fields are optional. Omit facts you cannot substantiate.

## Sources and claims

Every `sources` entry needs `resource`: a URL, internal path, `references/` path, or a non-followable scope descriptor. Add stable `id` when the body cites it; `title`, `author`, `usage_count`, and `last_modified` are optional objective signals, never a credibility score.

```yaml
sources:
  - id: policy
    resource: https://example.test/policy
    title: Revenue policy
    author: team:finance
    usage_count: 5000
    last_modified: 2026-05-30
usage_window: { from: 2026-06-01, to: 2026-06-30 }
```

`usage_window` frames all counts, though an entry may override it. Read use count as liveness/trend, not a universal ranking. Attribute claims through the ID, never a list position:

```markdown
This policy applies to recognized revenue.[^policy]

[^policy]: Revenue policy
```

Use links, not a `derived_from` field, to express lineage to internal concepts. See [paths and navigation](paths-and-navigation.md) for path forms.

## Generation and verification

```yaml
generated: { by: catalog_agent/1.4, at: 2026-06-20T22:53:05Z }
verified: { by: human:alex, at: 2026-06-25T09:00:00Z }
```

Both `generated.by` and ISO-8601 `generated.at` are required if `generated` exists. Each verification event has `by` and `at`; a bare mapping is a one-item list. Derive trust: no `verified` = unverified; non-human verification only = machine-confirmed; any `human:` verifier = human-reviewed. Trust is advisory and optional.

## Lifecycle

Use `status: draft`, `stable`, or `deprecated`; omitted means stable. Set `stale_after` to an absolute `YYYY-MM-DD` only; content is stale when `today >= stale_after`. `generated.at` measures content change, not re-confirmation.

## Actors

Spell actors as `<producer>/<version>`, `human:<id>`, or `process:<id>`. Use `human:` for people because trust classification relies on that prefix.
