# Windows GitHub Action Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a reproducible Windows package for this fork that compiles the refactored plugins, reuses the official Windows runtime base, repacks bot profiles, and publishes an artifact or tag release.

**Architecture:** A Windows PowerShell packaging script will download and extract the official `CS2BotImprover.zip` base package, use its managed API DLLs only as compile-time references, build the active plugins serially, overlay the new plugin/config/profile outputs, and create a new zip. A thin GitHub Actions workflow will run the script on `workflow_dispatch` and `v*` tags; tag runs will publish a GitHub Release using the fork's `GITHUB_TOKEN`.

**Tech Stack:** GitHub Actions, `windows-latest`, .NET 10 SDK, PowerShell, VPKEdit CLI, GitHub CLI.

---

### Task 1: Add the Windows packaging script

**Files:**
- Create: `scripts/package-windows.ps1`

- [ ] Download the selected upstream Windows base zip and VPKEdit CLI, verify the pinned VPKEdit SHA-256, and extract both into temporary directories.
- [ ] Validate that the base package contains RayTrace, BotController, CounterStrikeSharp runtime, and the shared API DLLs needed by the project references.
- [ ] Copy the base `RayTraceApi.dll` and `BotControllerApi.dll` into temporary source `libs` folders for compilation only.
- [ ] Restore and build `CompetitiveBotCore`, `BotAI`, `BotBuy`, `BotAimImprover`, `NadeSystem`, and `BotState` serially; run the core unit tests.
- [ ] Overlay plugin DLL/deps/PDB files and `CompetitiveBotCore.dll` into the matching base package plugin folders; copy NadeSystem grenade data and the competitive profile config.
- [ ] Convert each tracked `overrides/*/botprofile.db` into a Source 2 VPK v2 using `vpkeditcli.exe`, replacing the base VPKs and making Medium the active root VPK.
- [ ] Remove compile-only `libs` from the staged package, produce `CS2BotImprover-windows-<version>.zip`, and write a SHA-256 checksum.

### Task 2: Add the fork-aware GitHub Actions workflow

**Files:**
- Create: `.github/workflows/windows-package.yml`

- [ ] Run on `workflow_dispatch` with inputs for base repository, base tag, and optional release publication.
- [ ] Run automatically on `v*` tags and publish the generated zip/checksum as a release asset.
- [ ] Use `actions/checkout@v4`, `actions/setup-dotnet@v4`, and PowerShell on `windows-latest`.
- [ ] Set `contents: write` so the fork can publish its own release when a tag is pushed.
- [ ] Upload the zip/checksum as Actions artifacts for manual runs.

### Task 3: Verify the implementation

**Files:**
- Test: `.github/workflows/windows-package.yml`
- Test: `scripts/package-windows.ps1`

- [ ] Parse the workflow YAML and inspect the final diff for missing paths or untracked binaries.
- [ ] Run `git diff --check`.
- [ ] Run the existing 19 core unit tests and serial plugin builds locally with temporary external references.
- [ ] Verify the workflow's staging contract against the official v1.4.2 archive layout.

