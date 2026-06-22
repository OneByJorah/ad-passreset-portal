# Spec Gap — Phase 14 deployment-docs consolidation

> **Source:** verification of `docs/superpowers/specs/` against the code on 2026-06-22. Of four
> specs checked, three are fully implemented (Phase 11 LDAP, STAB-023 ServerManager) or
> implemented-with-a-deliberate-divergence (Phase 13 Admin UI). The **only genuinely
> unimplemented commitment** is a pair of documentation items from the Phase 14 hosting-modes
> spec (`docs/superpowers/specs/2026-04-22-phase-14-hosting-modes-design.md`). All Phase 14
> *code* (hosting-mode detection, Kestrel HTTPS, installer modes, self-signed fallback) shipped
> and is verified present.

**Goal:** Close the two outstanding Phase 14 documentation commitments: create the consolidated
`docs/Deployment.md` and reduce `docs/IIS-Setup.md` to a redirect stub pointing into it.

**Created:** 2026-06-22. **Urgency: low** — docs only; no code or behavior change.

---

## The gap (verified)

| Spec commitment | State | Evidence |
|---|---|---|
| `docs/Deployment.md` — consolidated deployment guide covering IIS / Service / Console modes (spec line ~215, success criterion 7) | **Missing** | `docs/Deployment.md` does not exist |
| `docs/IIS-Setup.md` reduced to a redirect stub → `docs/Deployment.md#iis-mode` (spec line ~227) | **Not done** | `docs/IIS-Setup.md` is still the full ~475-line guide |

Everything else in the Phase 14 spec is implemented: `HostingMode` enum, `HostingModeDetector`,
`KestrelHttpsCertOptions` + validator (skips IIS/Console, requires cert in Service mode),
`Program.cs` Service-mode Kestrel HTTPS wiring, installer `-HostingMode`/`-ServiceAccount`/
`-PfxPath`/`-AllowSelfSignedCertificate`, `Test-ServiceModePreflight`, `Install-AsWindowsService`,
unit + Pester tests.

---

### Task 1: Create `docs/Deployment.md`

- [ ] **Step 1: Read the source material**
  - `docs/superpowers/specs/2026-04-22-phase-14-hosting-modes-design.md` (the intended structure)
  - `docs/IIS-Setup.md` (existing IIS content to fold in)
  - `deploy/Install-PassReset.ps1` parameter block (the authoritative `-HostingMode`,
    `-ServiceAccount`, `-ServicePassword`, `-PfxPath`, `-PfxPassword`,
    `-AllowSelfSignedCertificate` flags) so the doc matches reality, not the spec's guess.
  - `src/PassReset.Web/Program.cs` Service-mode block (~line 494) for the cert-source truth:
    in Service mode the HTTPS cert comes from `Kestrel:HttpsCert` (Thumbprint from a store, or
    PfxPath), written by the installer — **not** from the checked-in appsettings templates.

- [ ] **Step 2: Write the guide** with three mode sections, each self-contained:
  - **IIS mode** (the default; fold in the current `IIS-Setup.md` content). Anchor: `#iis-mode`.
  - **Windows Service mode** — `-HostingMode Service`, service account, Kestrel-terminated TLS
    via `Kestrel:HttpsCert` (store thumbprint or PFX), self-signed fallback default-on, the
    IIS→Service migration teardown behavior.
  - **Console mode** — `-HostingMode Console`, intended use (debugging / non-IIS hosts).
  - A short "choosing a mode" intro and a cross-link to `docs/Admin-UI.md` and
    `docs/appsettings-Production.md`.

- [ ] **Step 3: Verify internal anchors/links resolve** (especially `#iis-mode`, since
  `IIS-Setup.md` will point at it).

### Task 2: Reduce `docs/IIS-Setup.md` to a redirect stub

- [ ] **Step 1: Confirm no other doc deep-links into IIS-Setup.md sections** that would break:
  `grep -rn "IIS-Setup.md" docs/ README.MD *.md` — note any anchors referenced and preserve or
  redirect them.
- [ ] **Step 2: Replace the body** with a short stub: a one-paragraph pointer to
  `docs/Deployment.md#iis-mode`, keeping the top-level heading so existing `IIS-Setup.md` links
  still land somewhere sensible. Do **not** delete the file (inbound links exist, e.g. installer
  output and README).

### Task 3: Cross-reference sweep

- [ ] Update any references that should now point at `docs/Deployment.md` (README deployment
  section, `CLAUDE.md` solution-layout doc list, installer `Write-Host` guidance if it names
  `IIS-Setup.md`). Keep changes minimal and accurate.

- [ ] **Commit** (single docs commit):
  ```bash
  git add docs/Deployment.md docs/IIS-Setup.md
  # plus any cross-ref files touched
  git commit -m "docs: add consolidated Deployment.md, stub IIS-Setup.md [phase-14 spec gap]"
  ```

---

## Deliberate divergences — recorded so they are NOT re-flagged as gaps

A spec-vs-code sweep flagged these; each was verified to be an **intentional post-spec decision**,
not an omission. Do not "fix" them back to the spec.

1. **`AdminSettings.Enabled` defaults to `false`, not `true` (spec said `true`).**
   Deliberate opt-in. The code's own XML doc says so
   (`src/PassReset.Web/Configuration/AdminSettings.cs:9`: "Defaults to `false` (opt-in)"), and
   `CLAUDE.md` documents it as the opt-in master flag. A config-editing UI defaulting **off** is
   the safer posture; the spec is the stale side here. **Leave as `false`.**

2. **`Test-IsDeserializedObject` was removed (STAB-023 spec said keep it defined-but-unused).**
   Deliberately removed in **v2.0.5** as code-review cleanup (see `CHANGELOG.md` 2.0.5: "removed
   the now-unused `Test-IsDeserializedObject` helper (the ServerManager approach has no
   deserialization to detect)"). The spec line predates that cleanup. **Do not re-add.**

3. **Phase 14 "`Kestrel:HttpsCert` missing from appsettings" is not a bug.**
   `Program.cs` reads `Kestrel:HttpsCert` **only** in Service mode (~line 494); the validator
   skips IIS/Console. The cert is provided by the **installer** at install time (`-PfxPath` /
   `-Thumbprint`), which is correct — a cert thumbprint should not ship in a checked-in template.
   IIS (default) and Console are unaffected. **No code change needed.**

---

## Self-review

- **Scope is honest and minimal:** after filtering three agent false-positives, the only real
  spec gap across all four specs is two Phase 14 doc files. This plan does exactly that, plus
  records the divergences so the next sweep doesn't reopen them.
- **Doc accuracy guard:** Task 1 Step 1 pins the doc to the installer + Program.cs reality, not
  the spec's pre-implementation guesses (the spec mispredicted the cert-config source).
- **No release coupling.**
