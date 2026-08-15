# Attested Computations

## Contents

- [Dedicated concept and contract](#dedicated-concept-and-contract)
- [Computation representation](#computation-representation)
- [Execution and review](#execution-and-review)
- Related: [concepts and conformance](concepts-and-conformance.md), [provenance, trust, and lifecycle](provenance-trust-lifecycle.md), [paths and navigation](paths-and-navigation.md)

## Dedicated concept and contract

Make every sanctioned computation its own `type: Attested Computation` concept; metrics and other concepts link to it. `runtime` is required and defines the parameters' binding semantics. The optional contract fields are:

| Field | Purpose |
| --- | --- |
| `parameters` | Typed holes: `{ name, type, required }`. |
| `computation` | Path to external computation content. |
| `executor` | `resource` for run instructions/code and required `receipt` fields. |
| `attester` | `resource` for deterministic, no-LLM receipt-checking code. |

```yaml
type: Attested Computation
runtime: bigquery
parameters:
  - { name: year, type: integer, required: true }
executor:
  resource: references/skills/run-on-bq.md
  receipt: [job_id, executed_sql, result]
attester:
  resource: references/attesters/revenue.py
```

## Computation representation

Choose one form: one fenced block beneath `# Computation` for inline code, or `computation` pointing to an external file and no body fence. Do not use both. Follow [paths and navigation](paths-and-navigation.md) for resource paths.

An agent may provide values only for declared parameters. It must not author, edit, or substitute the sanctioned computation; the executor binds values and the attester compares the expanded or compiled artifact in the receipt.

## Execution and review

Load the contract, provide declared values, execute through the executor, then run the deterministic attester. Surface a failed attestation and warn or refuse when stale. Keep these distinct:

- `verified` is a document-level confirmation that the definition remains correct.
- Attestation is per-run evidence that the sanctioned artifact produced this result.

The runtime protocol, receipt/verdict wire formats, attester ABI, sandboxing, and attestation caching are deliberately outside OKF v0.2.
