# Versioning and migration

## Contents

- [Version declaration](#version-declaration)
- [v01 to v02](#v01-to-v02)
- Related: [concepts and conformance](concepts-and-conformance.md), [paths and navigation](paths-and-navigation.md), [Attested Computations](attested-computations.md)

## Version declaration

This skill targets OKF 0.2. A minor release adds compatible optional fields or conventions; a major release may change required fields or reserved filenames. A bundle may state `okf_version: "0.2"` only in bundle-root `index.md` frontmatter. Consume unknown declared versions best-effort instead of rejecting them.

## v0.1 to v0.2

| v0.1 | v0.2 | Compatibility |
| --- | --- | --- |
| `timestamp` | `generated.at` | Consumers may fall back when `generated` is absent. |
| `# Citations` body list | `sources` frontmatter | Prefer sources; legacy lists remain readable. |
| — | `generated`, `verified`, `status`, `stale_after` | Additive optional metadata. |
| — | `Attested Computation` and `# Computation` | Additive. |

Retain old content unless migration is requested. For new content, write v0.2 forms and actor spelling from [provenance, trust, and lifecycle](provenance-trust-lifecycle.md).
