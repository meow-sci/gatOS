# Concepts and conformance

## Contents

- [Required shape](#required-shape)
- [Frontmatter and body](#frontmatter-and-body)
- [Preflight](#preflight)
- Related: [provenance, trust, and lifecycle](provenance-trust-lifecycle.md), [paths and navigation](paths-and-navigation.md), [Attested Computations](attested-computations.md), [versioning and migration](versioning-and-migration.md)

## Required shape

Every non-reserved `.md` file is a concept: UTF-8 Markdown with a parseable YAML frontmatter block delimited by `---` at the very start of the file. Its only universal requirement is a non-empty, descriptive `type`. `index.md` and `log.md` are reserved at every level and cannot be concepts.

Unknown types are valid. Preserve producer-defined frontmatter extensions; consumers must not reject them.

## Frontmatter and body

Add `title` (display name), one-sentence `description`, canonical `resource`, and short-string `tags` when helpful. Use structural Markdown (headings, lists, tables, fenced code). Conventional but optional headings are `# Schema`, `# Examples`, and `# Computation`.

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

Use `sources` and keyed footnotes for claim provenance; see [provenance, trust, and lifecycle](provenance-trust-lifecycle.md). Follow [Attested Computations](attested-computations.md) for that special type.

## Preflight

- Confirm every concept parses and has non-empty `type`.
- Confirm reserved files use [their dedicated structures](paths-and-navigation.md).
- Preserve unknown fields/types; accept missing optional metadata and broken links.
- Validate any computation contract and `sources` footnote-ID join.
