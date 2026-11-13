#!/usr/bin/env bash
# scripts/package.sh [package.zip]
# Builds an App Service package by hand: publish, then scripts/ci-package.ps1 (the same ffmpeg,
# font and smoke check the shared deploy.yml adds), then the zip. Needs Linux or WSL with pwsh
# (the ffmpeg and font are Linux ones). The publish folder is left at ./publish for inspection.
set -euo pipefail

TARGET="${1:-package.zip}"
PUBLISH=publish

# Framework-dependent on purpose: App Service supplies the DOTNETCORE|10.0 runtime.
rm -rf "$PUBLISH"
dotnet publish src/PoRedoMedia.Api -c Release -o "$PUBLISH"
pwsh -NoProfile ./scripts/ci-package.ps1 -PublishDir "$PUBLISH"

# The zip, written by Python so the bundled ffmpeg keeps its execute bit: Compress-Archive and
# Windows zip tools drop Unix modes.
python3 - "$PUBLISH" "$TARGET" <<'PY'
import os, sys, zipfile

root, target = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
    for folder, _, files in os.walk(root):
        for name in files:
            path = os.path.join(folder, name)
            entry = os.path.relpath(path, root).replace(os.sep, "/")
            info = zipfile.ZipInfo.from_file(path, entry)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.create_system = 3  # Unix, so the mode below is honoured
            info.external_attr = (0o755 if entry.startswith("ffmpeg/") else 0o644) << 16
            with open(path, "rb") as source:
                archive.writestr(info, source.read())
print(f"{target}: {os.path.getsize(target) // 1_000_000} MB")
PY
