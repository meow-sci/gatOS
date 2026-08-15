# Paths and bundle navigation

## Contents

- [Links and path fields](#links-and-path-fields)
- [References directory](#references-directory)
- [Index files](#index-files)
- [Log files](#log-files)
- Related: [concepts and conformance](concepts-and-conformance.md), [provenance, trust, and lifecycle](provenance-trust-lifecycle.md), [versioning and migration](versioning-and-migration.md)

## Links and path fields

A concept ID is its bundle-relative path without `.md`. Standard Markdown links make directed relationships; surrounding prose supplies the relationship meaning. Prefer bundle-root links for relocation stability:

```markdown
See the [customers table](/tables/customers.md).
```

Relative links are also valid. Broken links are valid, not malformed. `resource`, `sources[].resource`, `computation`, `executor.resource`, and `attester.resource` accept URLs, root-relative paths, or relative paths; a source resource may also be a scope descriptor.

## References directory

`references/` is an optional convention for mirrored external material, run instructions, or code. It has no special path semantics.

## Index files

An optional `index.md` supports progressive disclosure. It has no frontmatter except a bundle-root index may declare `okf_version: "0.2"`; its body groups linked items under headings and should include each concept description.

```markdown
# Tables

* [Customers](customers.md) - One row per customer.
```

Missing indexes are conformant; consumers may synthesize them.

## Log files

Optional `log.md` records the directory scope's newest-first updates. Date headings must be ISO `YYYY-MM-DD`; prose entries may use a bold action label but need not.

```markdown
# Directory Update Log

## 2026-05-22
* **Update**: Added [Customer Metrics](customer-metrics.md).
```
