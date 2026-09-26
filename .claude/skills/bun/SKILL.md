---
name: bun
description: how to use bun effectively for scripting and sqlite and more

---

# Bun Skill Guide

Use this skill when a task involves Bun runtime behavior, Bun-native APIs, shell scripting with Bun, file I/O, process execution, environment variables, or Bun's built-in data format and database support.

# Preferences

- MUST prefer Bun APIs for file I/O, process execution, environment variables, and data parsing when they simplify implementation and portability.
- MUST prefer Bun APIs for JSON, TOML, YAML parsing and serializing

## What Bun Covers

Bun provides:

- A fast JavaScript/TypeScript runtime and script runner
- Bun-native APIs for file I/O, process execution, HTTP/server work, and utilities
- A cross-platform shell API for scripting
- Native support for SQLite and common serialization formats
- Built-in parser and runtime integration for TOML/JSON5/YAML-style workflows
- Binary data primitives and compatibility with Node/Web APIs

## How Agents Should Use This Skill

1. Start with the high-level map in docs/bun-apis.md and docs/runtime.md.
2. Jump to the specific area using the table of contents below.
3. Read only the focused doc needed for the task to keep context efficient.
4. Prefer Bun-native APIs where they simplify implementation and portability.

## Complete Table Of Contents

### Core Runtime And API Surface

- [Bun APIs Overview](docs/bun-apis.md)
- [Bun Runtime (run files, scripts, watch mode, CLI behavior)](docs/runtime.md)
- [Globals (Bun and Web/Node-compatible globals)](docs/globals.md)
- [Utils (version, env alias, sleep, which, uuidv7, etc.)](docs/utils.md)
- [TypeScript With Bun](docs/typescript.md)

### Scripting, Commands, And Processes

- [Parse CLI args](docs/parse-args.md)
- [Shell (bun shell template literal API)](docs/shell.md)
- [Spawn (Bun.spawn and Bun.spawnSync)](docs/spawn.md)
- [Console (Bun-native console behavior)](docs/console.md)

### Files, Paths, And Archives

- [File I/O (Bun.file and Bun.write)](docs/file.md)
- [Glob (fast file matching and scanning)](docs/glob.md)
- [Archive (Bun.Archive for tar and tar.gz)](docs/archive.md)

### Data, Parsing, And Content Formats

- [Binary Data (ArrayBuffer, TypedArray, DataView, Blob, BunFile)](docs/binary-data.md)
- [JSON5 Support](docs/json5.md)
- [TOML Support](docs/toml.md)
- [YAML Page In Local Docs](docs/yaml.md)
- [Markdown API (Bun.markdown.html/render/react)](docs/markdown.md)

### Configuration And Security-Related Utilities

- [Environment Variables (.env loading and runtime access)](docs/env.md)
- [Hashing (Bun.password and Bun.hash)](docs/hashing.md)

### Database

- [SQLite (bun:sqlite)](docs/sqlite.md)

## Quick Routing By Task

- Need to run scripts or understand CLI behavior: [docs/runtime.md](docs/runtime.md)
- Need to execute system commands from TypeScript: [docs/shell.md](docs/shell.md) and [docs/spawn.md](docs/spawn.md)
- Need file reads/writes or copying data: [docs/file.md](docs/file.md)
- Need archive create/extract: [docs/archive.md](docs/archive.md)
- Need env loading semantics: [docs/env.md](docs/env.md)
- Need Bun-native database access: [docs/sqlite.md](docs/sqlite.md)
- Need hashing/password verification: [docs/hashing.md](docs/hashing.md)
- Need format parsing/import behavior: [docs/json5.md](docs/json5.md), [docs/toml.md](docs/toml.md), [docs/yaml.md](docs/yaml.md)

## Notes

- The local YAML doc currently appears to mirror the TOML content and title. Keep the link for completeness, but verify Bun YAML details against upstream docs when YAML-specific behavior matters.
- Each docs page includes a link to Bun's global docs index: https://bun.com/docs/llms.txt


