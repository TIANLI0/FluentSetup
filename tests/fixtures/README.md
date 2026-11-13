# Test fixtures

Served from `https://raw.githubusercontent.com/TIANLI0/FluentSetup/main/tests/fixtures/` to the end-to-end test of
built-in updates (`tests/Integration/e2e.ps1`):

- `update-feed.json`: an update feed whose download (the LICENSE of tag v1.0.0) matches its SHA-256 but is not a
  setup package, so `/install-update` must refuse it.
- `update-feed-badhash.json`: the same download with a wrong SHA-256, refused before it is opened.

`import/` holds installer scripts for `fluentsetup import` (`tests/Builder.Core.Tests/ImportTests.cs`): an Inno Setup
per-user script, an Inno Setup per-machine script with overrides and an include, and an NSIS Modern UI 2 script. The
build output they install from is not part of the fixtures (tests create it where they need it).
