# Repository guidance

- Read `README.md`, `AI_RULES.txt`, and `docs/SOURCE_LAYOUT.md` before changing the project.
- Production projects live in `src/`; maintained regression executables live in `tests/Regression/` and are included in the root solution.
- `tests/Legacy/` and `docs/history/` retain historical checks/reference material. They are not the maintained test suite.
- Run `tools/Build.ps1` and the applicable checks through `tools/Test.ps1`. Use `tools/Publish.ps1` for a desktop publish.
- Keep build output, screenshots, generated fixtures, local `.checks` snapshots, game archives, saves, and extracted workspaces out of Git.
- Preserve embedded UI resources and their attribution notices. Do not add extracted game data.
- Preserve unrelated user changes and the external `../workspace/` directory. Archive operations must not overwrite their input files.
- Keep namespace/resource identities stable unless a change explicitly requires migration; several regression checks inspect internal types by reflection.
- Show the diff for each change. Build and publish confirmed application changes where possible, as required by `AI_RULES.txt`.
