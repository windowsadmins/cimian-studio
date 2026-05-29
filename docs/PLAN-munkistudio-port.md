# Plan: Port munkistudio features to CimianStudio

Living roadmap for bringing the Swift / SwiftUI macOS app [munkistudio](https://github.com/rodchristiansen/munkistudio) to feature parity inside CimianStudio (WinUI 3 / .NET 10 on Windows).

## Goal

Close the feature gap with munkistudio so a Cimian repo admin on Windows has the same set of editor / browser / dashboard / cleanup capabilities a Munki admin gets on macOS, without paying a macOS-only tax (no .mobileconfig editor, no AutoPkg-specific tooling unless we add a Windows-equivalent pipeline).

## Non-goals

- 1:1 visual copy. WinUI 3 idioms (NavigationView, Mica, Fluent icons) win over recreating SwiftUI sidebars pixel-for-pixel.
- Profiles tab (`.mobileconfig` editor). Pure macOS concern; skip unless customers ask.
- AutoPkg promotion (`Promoter` tab) in the first pass. Cimian doesn't have AutoPkg upstream yet — revisit once a Windows equivalent exists.
- Dependency graph rendering as a richly-styled SwiftUI canvas. We can land a simple list/tree first, upgrade later.

## Source / target mapping

| munkistudio (Swift) | CimianStudio (C#) |
|---|---|
| `Sources/Core/Models/*` | `src/CimianStudio.Core/Models/*` |
| `Sources/Core/Services/*` (protocols) | `src/CimianStudio.Core/Services/I*Service.cs` |
| `Sources/Infra/*` (actors / concrete services) | `src/CimianStudio.Infrastructure/*` |
| `Sources/App/Features/<X>/<X>ListView.swift` | `src/CimianStudio/Views/<X>Page.xaml(.cs)` + `Views/<X>ViewModel.cs` |
| `Sources/App/Features/<X>/<X>DetailView.swift` | embedded panel in `<X>Page.xaml` or a paired `<X>DetailView.xaml` user control |
| `RepositoryStore.selectedItemID` (centralized selection) | existing `MainWindow` history + per-page selection bindings |

## Phasing

PR-per-feature, branched off `main`, target `main`, merged when CI is green and the smoke test passes.

### Phase 1 — Faceted browsers (the easy wins)

Smallest payoff-to-effort ratio. Each is a one-screen NavigationView page that re-projects already-loaded packages. No new services, no filesystem writes.

1. **Icons** (`feature/icons-browser`) — list of icon files + image preview + rebuild-hashes action. Adds `IIconService` / `FileIconService`. *Small + a bit of new I/O.*
2. **Categories** (`feature/categories-browser`) — read-only group-by-category facet. No new services. *Small.*
3. **Developers** (`feature/developers-browser`) — same shape as Categories. *Small.*

### Phase 2 — Aggregates and tooling

Builds on Phase 1's facet machinery to surface repo-wide insight, and adds a write-path operation that needs careful confirmation UX.

4. **Dashboard** (`feature/dashboard`) — landing page replacing `WelcomePage` once a repo is open: counts, recent commits (reuses `GitService`), orphan-packages, category/developer top-N, largest packages. *Medium.*
5. **Clean tool** (`feature/clean-tool`) — preview + run `repoclean`-equivalent, keep-N-versions stepper, run history. Mirrors Build page's process-runner pattern. *Medium.*

### Phase 3 — Larger, optional

Heavier code, lower urgency. Tackle if there's appetite and CI cycles to spare.

6. **Dependencies view** (`feature/dependencies-view`) — start with a flat list of `requires` / `update_for` edges and a manifest-includes tree (TreeView). Defer custom Canvas graph rendering.
7. **Script syntax highlighting** (`feature/script-syntax-highlighting`) — extend the existing `PwshHighlighter` / `PwshLinter` pair into something that can color `bash`/`pwsh`/`cmd` script blocks in the package editor.

### Explicitly deferred / dropped

- Profiles (`.mobileconfig`) — macOS-only.
- Promoter (AutoPkg) — pipeline absent on Windows.
- Full SwiftUI-style graph canvas for Dependencies — revisit after the list view ships and we see whether anyone uses it.

## Per-feature plans

### 1. Icons browser

**Goal**: list of all icons under `<repo>/icons/`, preview the selected one, and let the user regenerate `_icon_hashes.plist`.

**Models** — new in `src/CimianStudio.Core/Models/Icons/`:
- `IconAsset.cs` — `Filename` (relative to `icons/`, includes any subfolder), `ByteSize`, nullable `Sha256`, nullable `LastModified`.

**Services**:
- `ICimianStudio.Core.Services.IIconService` — `Task<IReadOnlyList<IconAsset>> ListAsync(CimianRepository repo, CancellationToken ct)`, `Task<byte[]> ReadBytesAsync(CimianRepository repo, string filename, CancellationToken ct)`, `Task RebuildHashesAsync(CimianRepository repo, IProgress<int>? progress, CancellationToken ct)`.
- Implementation in `src/CimianStudio.Infrastructure/Icons/FileIconService.cs`. Walk `icons/` with `Directory.EnumerateFiles(..., SearchOption.AllDirectories)`, filter on `.png`/`.jpg`/`.jpeg`/`.icns`. Hashes via `System.Security.Cryptography.SHA256.HashDataAsync`. Write `_icon_hashes.plist` via a small hand-rolled XML writer — the file is a trivial `<plist><dict>filename → hash</dict></plist>` shape, so no `plistlib` dependency needed.

**UI** — new `src/CimianStudio/Views/IconsPage.xaml(.cs)` + `ViewModels/IconsViewModel.cs`:
- Layout: `Grid` with two columns (`ListView` of filenames on the left, image + metadata panel on the right). Header shows "Icons (N)" and a `Button` "Rebuild icon hashes" (Lucide refresh-cw glyph). Disabled while rebuilding; show inline `ProgressRing` until done.
- Image preview: `Image` with `Source` bound to a `BitmapImage` loaded async from `IIconService.ReadBytesAsync`. Cap at 280×280 like munkistudio. Below: filename and SHA-256 in a monospaced `TextBlock`.
- Add `NavIcons` `NavigationViewItem` (Lucide `image` glyph) in `MainWindow.xaml`; wire through `NavigateTo("icons")` and `ResolvePage("icons")`.

**Cross-feature**: pkginfo `IconName` / `IconHash` fields will eventually link here; not in scope for this PR.

### 2. Categories browser

**Goal**: faceted view — pick a category, see the packages in it.

No new models or services. Pure projection over `PackagesViewModel`'s loaded set.

**UI** — new `Views/CategoriesPage.xaml(.cs)` + `ViewModels/CategoriesViewModel.cs`:
- Group-by `Package.Category ?? "Uncategorized"`, sort by category name.
- `Grid` two-column: left `ListView` of `(CategoryName, PackageCount)`, right `ListView` of packages (name + version) in the selection.
- Clicking a package in the detail list navigates to the Packages page with that package selected — reuse the existing `MainWindow.NavigateToPackage(...)` helper.
- Add `NavCategories` (Lucide `tags` glyph).

### 3. Developers browser

Mirror of Categories with `Package.Developer ?? "Unknown"`. Same layout, separate page (`Views/DevelopersPage.xaml`), separate view model. Lucide `users` glyph.

### 4. Dashboard

**Goal**: useful landing page once a repo is open. Surfaces repo health at a glance.

**Sections** (each as a Fluent "card" — `Border` with rounded corners + subtle background):
- Counts: packages / manifests / catalogs / icons.
- Recent commits (top 5, from `IGitService.GetRecentCommitsAsync`).
- Orphans: packages not referenced by any manifest, and manifest entries pointing at missing packages.
- Top categories (top 5 by count).
- Top developers (top 5 by count).
- Largest packages (top 5 by installer file size, if known).

**Wiring**: navigate here automatically when a repo opens, replacing the current default to `RepositoryPage`. Existing `RepositoryPage` becomes "Repository info / actions" accessible from the nav.

### 5. Clean tool

**Goal**: preview which old package versions would be removed and let the user execute the clean.

**Design**:
- Two-mode page (Preview / History) selected via `SegmentedControl`-like toggle.
- Preview mode: keep-N stepper (default 3), "Run preview" button → table of `(package, kept versions, removed versions, freed bytes)`. "Execute clean" button gated by a `ContentDialog` confirming the destructive action.
- History mode: list of past clean runs with timestamps + freed-bytes totals, persisted to `<repo>/.cimianstudio/clean-history.jsonl`.

**Service**: `ICleanService` in Core, `CleanService` in Infrastructure. Reuses the pkginfo loader to enumerate per-name versions; deletes via `File.Delete` after the confirmation. Writes a JSONL entry per run.

### 6. Dependencies view (Phase 3, lean cut)

**Goal**: surface `requires` and `update_for` relationships without committing to a custom graph canvas.

- Top half: `TreeView` of "manifest → included_manifests → managed_installs/managed_updates/optional_installs" with each leaf clickable to jump to the package editor.
- Bottom half: flat `ListView` of `(package, requires[]), (package, update_for[])` rows, filterable by package name.
- Defer a true graph (nodes + edges) until users ask for it.

### 7. Script syntax highlighting (Phase 3)

Extend the existing `PwshHighlighter` to a `ScriptHighlighter` with per-language keyword/operator tables for `bash`, `cmd`, `pwsh`. Apply in the package editor's pre/postinstall script `RichEditBox`. No new pages; pure editor polish.

## PR conventions

- One feature per PR. Branch name `feature/<feature>`.
- Each PR's body uses the same template the existing repo PRs use: Summary bullets + Test plan checklist.
- Each feature gets a checkbox in this doc, updated in the same PR that lands the feature.
- Commits keep the existing tone: short subject ("icons: list view + rebuild-hashes action"), squashed to one when merged.

## Status checklist

- [ ] 1. Icons browser
- [ ] 2. Categories browser
- [ ] 3. Developers browser
- [ ] 4. Dashboard
- [ ] 5. Clean tool
- [ ] 6. Dependencies view (lean)
- [ ] 7. Script syntax highlighting

Last updated: 2026-05-24.
