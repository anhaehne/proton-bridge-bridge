# C# and .NET conventions

Apply to backend, AppHost, service defaults, and C# tests. Follow `.editorconfig`,
Microsoft .NET conventions, and the .NET runtime style guide, with these repository
choices taking precedence. Keep style refactors behavior-preserving.

1. **Layout:** Use four spaces, LF, a final newline, Allman braces, and braces for
   every control-flow body. Put each statement/local declaration on its own line.
   Use file-scoped namespaces and sorted using directives outside them, System
   first. Keep simple properties and short expression-bodied members compact.
   Separate distinct members and logical phases with one blank line; keep related
   statements/fields together. Avoid padding inside braces, between attributes and
   declarations, or between every statement. Wrap naturally, with continued binary
   operators first; do not force documentation line limits on code or literals.
2. **Names and types:** Use PascalCase for types, methods, public members, and
   constants; camelCase for parameters/locals; _camelCase for private instance fields;
   s_camelCase for private static fields; I-prefixed interfaces; Async-suffixed async
   methods. Preserve external contract names. Prefer var whenever possible, explicit
   types when clarity requires them. Never change inferred types just for style.
   Use meaningful names, C# keywords, nullable annotations, and pinned-SDK features.
   Do not add `#nullable enable` or suppress warnings. Favor straightforward flow
   and small cohesive methods.
3. **Async/tooling:** Public async service/controller methods should accept and
   forward CancellationToken where practical. Never use `.Result`, `.Wait()`, or
   sync-over-async. Use the Aspire MCP server when available to run AppHost.
4. **EF Core:** Manage migrations exclusively through `dotnet ef`; never manually
   create, edit, or delete migration files. Use EF Core queries/update APIs. Raw SQL
   requires necessity and documentation explaining why supported APIs cannot express
   the operation.
5. **Comments/checks:** Explain intent, invariants, constraints, and tradeoffs with
   adjacent standalone `//` comments, using sentence capitalization and punctuation.
   Separate phase comments from preceding code with one blank line. Avoid obvious
   narration, commented-out code, decorative separators, author/date headers, and
   stale TODOs. Use XML documentation for reusable public contracts when callers
   need semantics, units, preconditions, results, or exceptions; skip empty summaries
   for obvious properties/internal plumbing. Keep extended design in contract docs.
   Run documented formatting/brace verification and required project checks; manually
   review logical grouping and comment quality.
