#!/bin/bash
# Create a minimal Godot PCK file containing mod_manifest.json
# PCK format: https://docs.godotengine.org/en/stable/contributing/development/file_formats/pck.html
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
OUT_DIR="$PROJECT_DIR/out"
PCK_FILE="$OUT_DIR/SpireBridge.pck"

mkdir -p "$OUT_DIR"

# Write the manifest
MANIFEST='{"pck_name":"SpireBridge","name":"SpireBridge","author":"nkhoit","description":"WebSocket bridge for programmatic game access","version":"0.1.0"}'

# Use Python to create the PCK binary
python3 << 'PYEOF'
import struct
import os

out_dir = os.environ.get("OUT_DIR", os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "out"))
pck_path = os.path.join(out_dir, "SpireBridge.pck")

manifest = b'{"pck_name":"SpireBridge","name":"SpireBridge","author":"nkhoit","description":"WebSocket bridge for programmatic game access","version":"0.1.0"}'

# File path in PCK (must be res://mod_manifest.json)
file_path = "res://mod_manifest.json"
file_path_bytes = file_path.encode("utf-8")

# PCK format (Godot 4.x):
# Header:
#   magic: 0x43504447 ("GDPC")
#   format_version: 2 (Godot 4)
#   ver_major: 4
#   ver_minor: 0
#   ver_patch: 0
#   flags: 0
#   files_base_offset: 0 (unused in format 2, but present)
#   reserved: 16 bytes of 0
#
# File count: uint32
# For each file:
#   path_len: uint32 (padded to 4 bytes)
#   path: bytes (padded to 4 bytes)
#   offset: uint64
#   size: uint64
#   md5: 16 bytes
#
# Then file data

import hashlib

md5 = hashlib.md5(manifest).digest()

# Calculate sizes
path_padded_len = len(file_path_bytes)
if path_padded_len % 4 != 0:
    path_padded_len += 4 - (path_padded_len % 4)

# Header: 4+4+4+4+4+4+8+16*4 = 96 bytes... let me be precise
# magic(4) + format_version(4) + ver_major(4) + ver_minor(4) + ver_patch(4) + flags(4) + files_base(8) + reserved(64)
header_size = 4 + 4 + 4 + 4 + 4 + 4 + 8 + 64  # = 96

# File table entry: path_len(4) + path(padded) + offset(8) + size(8) + md5(16)
file_entry_size = 4 + path_padded_len + 8 + 8 + 16

# Number of files
num_files = 1

# Total header + file table
table_size = header_size + 4 + file_entry_size  # +4 for file count

# File data starts after table
data_offset = table_size

with open(pck_path, "wb") as f:
    # Magic
    f.write(struct.pack("<I", 0x43504447))
    # Format version (2 for Godot 4)
    f.write(struct.pack("<I", 2))
    # Engine version
    f.write(struct.pack("<I", 4))  # major
    f.write(struct.pack("<I", 4))  # minor
    f.write(struct.pack("<I", 0))  # patch
    # Flags
    f.write(struct.pack("<I", 0))
    # Files base offset
    f.write(struct.pack("<Q", 0))
    # Reserved (16 uint32s = 64 bytes)
    f.write(b'\x00' * 64)

    # File count
    f.write(struct.pack("<I", num_files))

    # File entry
    f.write(struct.pack("<I", len(file_path_bytes)))
    f.write(file_path_bytes)
    # Pad to 4-byte alignment
    padding = path_padded_len - len(file_path_bytes)
    if padding > 0:
        f.write(b'\x00' * padding)
    # Offset (absolute position in file)
    f.write(struct.pack("<Q", data_offset))
    # Size
    f.write(struct.pack("<Q", len(manifest)))
    # MD5
    f.write(md5)

    # File data
    f.write(manifest)

print(f"Created {pck_path} ({os.path.getsize(pck_path)} bytes)")
PYEOF
