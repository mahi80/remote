#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"

CSC="/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe"
REF="-r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll -r:System.Net.Security.dll -r:System.Security.Cryptography.X509Certificates.dll"
COMMON="-nologo -langversion:5 -optimize+ -warn:3"

mkdir -p bin

# csc.exe truncates forward-slash paths, so hand it Windows-style ones
srcs() { local f; for f in "$@"; do printf '%s ' "${f//\//\\}"; done; }

echo "host..."
"$CSC" $COMMON -target:winexe -out:bin/RemoteDesktop.Host.exe -main:Host.HostMain \
    $REF $(srcs src/Shared/*.cs src/Host/*.cs)

echo "viewer..."
"$CSC" $COMMON -target:winexe -out:bin/RemoteDesktop.Viewer.exe -main:Viewer.ViewerMain \
    $REF $(srcs src/Shared/*.cs src/Viewer/*.cs)

echo "smoke..."
"$CSC" $COMMON -target:exe -out:bin/Smoke.exe -main:Smoke.Program \
    $REF $(srcs src/Shared/*.cs src/Host/*.cs src/Viewer/*.cs src/Smoke/*.cs)

echo "built -> bin/"
