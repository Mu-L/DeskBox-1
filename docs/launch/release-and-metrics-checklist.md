# DeskBox 1.3.8 International Launch Checklist

This checklist treats 1.3.8 as the only application, website, update-manifest, and distribution version for the campaign. Later small application fixes may change the repository, but campaign copy should not drift to another version unless a new public installer is intentionally released.

## Release gate

- [ ] x64 unit tests pass with `Platform=x64`.
- [ ] Canonical Debug build starts from this repository and its executable path is verified.
- [ ] x64 and ARM64 Release publishes use matching platform and runtime identifiers.
- [ ] Both Inno Setup installers compile with version 1.3.8.
- [ ] SHA-256 and size are recorded for both installers.
- [ ] Authenticode status is recorded for both installers.
- [ ] GitHub Release notes contain no candidate wording, open-issue status, or pending artifact placeholders.
- [ ] README badge says Windows 10/11, and public language copy states the current localization scope accurately.
- [ ] SmartScreen is tested on a clean Windows machine or Windows Sandbox after signing and public hosting.
- [ ] Win+D is tested in dynamic and desktop-pinned modes; GitHub issue #47 remains open during validation.
- [ ] Stable update manifest selects independent x64 and ARM64 URL, SHA-256, and size values.
- [ ] Direct upgrade from 1.3.7 preserves settings, widgets, storage, and installation path.
- [ ] A genuinely new profile receives one initial file widget on interactive launch.
- [ ] A startup launch does not show onboarding; the next interactive launch shows it once.
- [ ] Deleting all file widgets does not recreate one after restart.

## Funnel gate

- [x] English positioning leads with Windows desktop file organization.
- [x] Public maker identity is standardized as Tianyu Zhu.
- [x] GitHub profile README is English-first.
- [x] Repository description and Topics use current product and .NET 10 language.
- [x] Website source includes a maker-oriented newsletter form.
- [x] Website source includes `/en/press/` and `/en/privacy/`.
- [ ] Register the `tianyuzhu` Buttondown account and test double opt-in, unsubscribe, and sender identity.
- [ ] Configure a working media email at `hello@deskbox.fun` or keep the verified QQ address in public material.
- [ ] Deploy the website and test the complete subscribe flow from a private browser window.
- [ ] Publish the 60–90 second master demo and 15–25 second silent cut.

## Permanent distribution nodes

- [ ] Publish DeskBox 1.3.8 GitHub Release assets and SHA-256 sidecars.
- [ ] Update every configured Microsoft Store listing locale to v1.3.8; do not upload the older v1.3.7 export or the v1.3.9 draft export.
- [ ] Submit and validate `TianyuZhu.DeskBox` in `microsoft/winget-pkgs`.
- [ ] Add DeskBox to AlternativeTo and request comparison links to relevant desktop organizers.
- [ ] Create Product Hunt product and maker profiles; schedule only after the press page is live.
- [ ] Submit to selected editorial directories that link to official assets without repackaging.

## Staged launch

- [ ] Week 1 — release, website, newsletter, press kit, personal identity.
- [ ] Week 2 — WinGet, AlternativeTo, selected software directories.
- [ ] Week 3 — Windows and open-source Reddit stories on separate days.
- [ ] Week 4 — Product Hunt launch and maker comment.
- [ ] Week 5 — Show HN technical story.
- [ ] Weeks 5–6 — personalized outreach to Windows, open-source, WinUI, .NET, German, Japanese, and Brazilian Portuguese creators.
- [ ] Final — publish a retrospective and direct readers to the next-product mailing list.

## Metrics baseline and weekly log

Record a baseline immediately before the first external post, then update once per week. Targets are hypotheses, not promises.

| Metric | Baseline date | Baseline | Week 1 | Week 2 | Week 4 | Week 6 | Directional target |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| English email subscribers | 2026-08-06 | 0 |  |  |  |  | 200–500 |
| Independent English pages mentioning DeskBox | 2026-08-06 | To audit |  |  |  |  | 10–20 |
| Independent demo or review videos | 2026-08-06 | To audit |  |  |  |  | 3–5 |
| GitHub Stars | 2026-08-06 | 258 |  |  |  |  | 800–1,500 |
| GitHub Release downloads | 2026-08-06 | Capture at launch |  |  |  |  | Observe trend |
| English Microsoft Store reviews | 2026-08-06 | Capture at launch |  |  |  |  | About 20 |
| Creator-profile followers | 2026-08-06 | Capture at launch |  |  |  |  | Observe trend |
| Search presence: `DeskBox Windows` | 2026-08-06 | Capture screenshots |  |  |  |  | Top branded results owned |
| Search presence: `open-source Fences alternative` | 2026-08-06 | Capture screenshots |  |  |  |  | At least one durable result |

## Attribution notes

- Give each channel a distinct landing query parameter, for example `?utm_source=producthunt&utm_medium=launch&utm_campaign=deskbox_138`.
- Record email subscriber growth by launch day and source link; do not buy lists or import contacts without consent.
- Count only independent pages and videos. DeskBox-owned pages, mirrors, scraped release pages, and duplicate syndication do not count as third-party proof.
- Store review, star, and download targets must never be used as promised marketing claims.
