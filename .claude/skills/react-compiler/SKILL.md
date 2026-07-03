---
name: react-compiler
description: React Compiler automatically memoizes React components at build time, eliminating manual useMemo/useCallback/React.memo. Use when asked about React Compiler setup, memoization automation, "use memo"/"use no memo" directives, incremental adoption, compiler debugging, or build tool integration (Babel, Vite, Next.js, Expo, Metro, Rspack, Rsbuild).
---

React Compiler is a build-time optimizer that automatically handles memoization, letting you write plain React without manual `useMemo`, `useCallback`, or `React.memo`.

## Skill Files

| File | When to read |
|------|-------------|
| [introduction.md](./introduction.md) | What the compiler does, before/after examples, useMemo/useCallback guidance |
| [installation.md](./installation.md) | Setup for Babel, Vite, Next.js, React Router, Expo, Metro, Rspack, Rsbuild; ESLint integration; verifying setup |
| [incremental-adoption.md](./incremental-adoption.md) | Directory-based Babel overrides, `"use memo"` opt-in annotation mode, runtime feature flags/gating |
| [debugging.md](./debugging.md) | Compiler errors vs runtime issues, `"use no memo"` isolation, filing bug reports |
| [index.md](./index.md) | High-level topic map linking all sections |

## Key Concepts

- **Auto-memoization** — compiler inserts cache slots (`_c()`) automatically; no manual hooks needed
- **`"use memo"`** — opt a component/hook *into* compilation when using `compilationMode: 'annotation'`
- **`"use no memo"`** — opt a component *out* of compilation to isolate bugs
- **Rules of React** — compiler skips components that violate the rules; ESLint plugin surfaces violations
- **Babel-first** — must run before other Babel plugins; swc/oxc support in progress
- **React DevTools** — compiled components show a "Memo ✨" badge confirming optimization

## External Reference Docs

These reference pages are not bundled in this skill — link out to the React docs site:

- [Configuration options](https://react.dev/reference/react-compiler/configuration)
- [React version compatibility / target](https://react.dev/reference/react-compiler/target)
- [Directives reference](https://react.dev/reference/react-compiler/directives)
- [Compiling libraries](https://react.dev/reference/react-compiler/compiling-libraries)
- [Rules of React](https://react.dev/reference/rules)
- [React Developer Tools](https://react.dev/learn/react-developer-tools)
