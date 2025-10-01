# transform2D

English | [中文说明](README.zh-CN.md)

I am lazy so I ve used AI to generate readme



A lightweight (work‑in‑progress) Unity (C#) project exploring common 2D geometric and coordinate transformations.

> Status: Early WIP. Some planned utilities and demos are missing. Crash logs exist (historical). Documentation and tests welcome.

## Goals
- Reusable utilities for 2D affine transforms (translate / rotate / scale / pivot).
- Coordinate conversion helpers (local ↔ world, world ↔ screen/UI).
- Small demo scenes that visualize each concept.
- Encourage community collaboration (PRs for math correctness, performance, docs).

## Contributing
1. Fork
2. `git checkout -b feat/your-feature`
3. Implement changes (scripts / docs / tests)
4. Test in Unity (play mode sanity)
5. Commit: `feat: add pivot rotation demo`
6. Push & open a Pull Request
7. Respond to review

### Guidelines
- Keep functions small & pure (where possible).
- Public methods: XML doc comments (`///`).
- Demo scenes: prefix with `Demo_`.
- Avoid large binaries unless essential.
- Organize under `Assets/Scripts/<Category>/`.
- Add SPDX header to new C# files:
  ```csharp
  // SPDX-License-Identifier: MIT
  ```

## Handling Crash Logs
- If still useful: analyze & summarize root causes.
- If obsolete: move to `Docs/CrashLogs/` or remove via PR.

## License (MIT)
This project is licensed under the MIT License. See [LICENSE](LICENSE).

## Call for Contributions
If you enjoy numerical methods, visualization, or educational tooling—PRs are very welcome.

Star ⭐ the repo if it helps you. Open an Issue before starting large refactors.

PRs Welcome!
