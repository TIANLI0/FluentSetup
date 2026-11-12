# Test fixtures

Served from `https://raw.githubusercontent.com/TIANLI0/FluentSetup/main/tests/fixtures/` to the end-to-end test of
built-in updates (`tests/Integration/e2e.ps1`):

- `update-feed.json`: an update feed whose download (the LICENSE of tag v1.0.0) matches its SHA-256 but is not a
  setup package, so `/install-update` must refuse it.
- `update-feed-badhash.json`: the same download with a wrong SHA-256, refused before it is opened.
