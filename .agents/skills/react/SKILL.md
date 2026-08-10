---
name: react
description: React Rules of React — strict adherence required for React Compiler compatibility. Covers purity, idempotency, immutability, hook call rules, and component rendering rules. Use when writing any React component, custom hook, or reviewing React code.
---

## Purpose

Enforce the **Rules of React** so that React Compiler can automatically memoize components and hooks at build time, eliminating the need for manual `useMemo`/`useCallback`/`React.memo`. Violations of these rules cause incorrect memoization and subtle bugs.

## When this skill applies

- Writing or reviewing any React function component or custom hook
- Debugging unexpected re-renders, stale state, or memoization problems
- Evaluating whether code is React Compiler-compatible
- Any time you see `use client`, `useState`, `useEffect`, `useMemo`, `useCallback`, or custom `use*` hooks

## The Three Pillars (read the detail files)

| Pillar | File | Key concern |
|--------|------|-------------|
| Components and Hooks must be pure | [components-and-hooks-must-be-pure.md](./components-and-hooks-must-be-pure.md) | Idempotency, no side effects in render, immutability |
| React calls Components and Hooks | [react-calls-components-and-hooks.md](./react-calls-components-and-hooks.md) | Never call components directly, never pass hooks as values |
| Rules of Hooks | [rules-of-hooks.md](./rules-of-hooks.md) | Top-level only, React functions only |
| Overview / index | [index.md](./index.md) | Summary of all rules with links to sections |

---

## Critical rules — enforce these without exception

### Purity

- **Components and hooks must be idempotent.** Same inputs (props, state, context, args) → same output, every time. `new Date()`, `Math.random()`, and any non-deterministic calls must live in `useEffect` or event handlers — never in render.
- **No side effects in render.** DOM writes (`document.title = …`), network calls, subscriptions, and logging must not happen at the top level of a component function. Use `useEffect` or event handlers.
- **Local mutation is fine.** Creating and mutating a local array or object *within* a single render call is allowed — it never escapes the render scope.

### Immutability

- **Never mutate props.** Derive a new value instead: `const url = new Url(item.url, base)` not `item.url = …`.
- **Never mutate state directly.** Always use the setter from `useState`. Mutating the state variable does not trigger a re-render.
- **Never mutate hook arguments or return values.** Spread (`{ ...icon }`) before modifying. Hook internals may memoize based on reference identity — mutating breaks that.
- **Never mutate values after passing them to JSX.** Create separate objects for each consumer before the JSX expression that uses them.

### React controls rendering

- **Never call component functions directly.** `<Article />` yes, `Article()` no. Direct calls bypass React's reconciler, break hook rules, and prevent optimizations.
- **Never pass hooks as values.** No higher-order hooks (`withLogging(useData)`), no hook props (`<Button useData={hook} />`). Hooks are always called inline and statically.
- **Hooks must be static.** The set of hooks a component calls must be the same on every render — no conditional hook creation.

### Rules of Hooks (call-site rules)

- **Top level only.** Never inside `if`, `for`, `while`, nested functions, event handlers, `try/catch/finally`, `useMemo`, `useReducer`, or `useEffect` callbacks.
- **React functions only.** Only call hooks from function components or custom hooks (functions whose name starts with `use`). Never from plain JS functions, class components, or utilities.
- After a conditional early `return` — move any hook call *before* the return.

---

## React Compiler compatibility checklist

Before marking code as done, verify:

- [ ] No `new Date()` / `Math.random()` in render body
- [ ] No prop or state mutation
- [ ] No DOM side effects at top level
- [ ] All hooks called unconditionally at top level
- [ ] Components only used as JSX, never called as functions
- [ ] No hook passed as a prop or stored in a variable then called later
- [ ] Hook arguments not mutated after being passed
- [ ] Values not mutated after being passed to JSX

> If any item fails, **fix the violation** rather than adding a `// eslint-disable` or `"use no memo"` directive. Reserve `"use no memo"` only for third-party code you cannot change.

---

## ESLint enforcement

Install `eslint-plugin-react-hooks` and enable:

```json
{
  "rules": {
    "react-hooks/rules-of-hooks": "error",
    "react-hooks/exhaustive-deps": "warn"
  }
}
```

Use React's Strict Mode (`<StrictMode>`) in development — it double-invokes render to surface impure components.
