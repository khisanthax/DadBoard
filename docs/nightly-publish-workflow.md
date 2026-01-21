# Nightly Publish Workflow

This repo uses a scripted nightly publish so the same steps are repeatable and release artifacts never land in the git working tree.

## Command
Run from the repo root:

```powershell
powershell -ExecutionPolicy Bypass -File tools/publish_nightly.ps1
```

## What it does
- Computes the next nightly version (based on existing nightly release assets)
- Publishes:
  - DadBoard.exe (modules/Spine/App)
  - DadBoardUpdater.exe (modules/Spine/Updater)
  - DadBoardSetup.exe (modules/Spine/Setup)
- Validates version stamps
- Builds the payload zip + latest.json
- Uploads artifacts to the `nightly` release on GitHub
- Cleans up all release artifacts (zip/exe/latest.json/release_payload)

## Notes
- Requires GitHub CLI (`gh`) authenticated with access to `khisanthax/DadBoard`.
- The script always cleans release artifacts after upload.
