#!/usr/bin/env python3
"""Extract GLB images as 1024-square PNGs strictly below 2,000,000 bytes."""

import argparse
import base64
import io
import json
from pathlib import Path
import re
import struct
import sys
import tempfile
from urllib.parse import unquote, unquote_to_bytes, urlsplit
import zipfile

from PIL import Image

SIZE = (1024, 1024)
MAX_BYTES = 2_000_000


def read_glb(path):
    data = path.read_bytes()
    if len(data) < 12:
        raise ValueError("GLB header is truncated")
    magic, version, length = struct.unpack_from("<4sII", data)
    if magic != b"glTF" or version != 2 or length != len(data):
        raise ValueError("Expected a valid GLB 2.0 file with matching length")
    chunks = []
    offset = 12
    while offset < length:
        if offset + 8 > length:
            raise ValueError("Truncated chunk header")
        size, kind = struct.unpack_from("<II", data, offset)
        offset += 8
        if size % 4 or offset + size > length:
            raise ValueError("Invalid chunk length")
        chunks.append((kind, data[offset:offset + size]))
        offset += size
    if not chunks or chunks[0][0] != 0x4E4F534A:
        raise ValueError("Missing first JSON chunk")
    bins = [body for kind, body in chunks if kind == 0x004E4942]
    if len(bins) > 1:
        raise ValueError("Multiple BIN chunks are unsupported")
    return json.loads(chunks[0][1]), bins[0] if bins else b""


def read_uri(uri, directory):
    if uri.startswith("data:"):
        header, payload = uri.split(",", 1)
        raw = unquote_to_bytes(payload)
        return base64.b64decode(raw, validate=True) if header.endswith(";base64") else raw
    parsed = urlsplit(uri)
    if parsed.scheme or parsed.netloc or parsed.query or parsed.fragment:
        raise ValueError("Only data URIs and local relative image/buffer paths are supported")
    path = (directory / unquote(parsed.path)).resolve()
    if not path.is_relative_to(directory.resolve()):
        raise ValueError("External resource must be inside the GLB directory")
    return path.read_bytes()


def get_item(items, index):
    if type(index) is not int or not 0 <= index < len(items):
        raise ValueError(f"Invalid resource index: {index!r}")
    return items[index]


def image_payloads(document, binary, directory):
    buffers = {}
    for index, entry in enumerate(document.get("images", [])):
        if "uri" in entry:
            payload = read_uri(entry["uri"], directory)
        else:
            view = get_item(document.get("bufferViews", []), entry["bufferView"])
            buffer_index = view["buffer"]
            buffer = get_item(document.get("buffers", []), buffer_index)
            if buffer_index not in buffers:
                if "uri" in buffer:
                    raw = read_uri(buffer["uri"], directory)
                elif buffer_index == 0:
                    raw = binary
                else:
                    raise ValueError("Only buffer 0 may use the GLB BIN chunk")
                declared = buffer["byteLength"]
                if type(declared) is not int or declared < 0 or declared > len(raw):
                    raise ValueError("Invalid buffer length")
                buffers[buffer_index] = raw[:declared]
            raw = buffers[buffer_index]
            start, count = view.get("byteOffset", 0), view["byteLength"]
            if (type(start) is not int or type(count) is not int or
                    start < 0 or count < 0 or start + count > len(raw)):
                raise ValueError("Image bufferView exceeds its buffer")
            payload = raw[start:start + count]
        yield index, entry.get("name", f"image_{index}"), payload


def encode_png(image):
    output = io.BytesIO()
    image.save(output, format="PNG", optimize=True, compress_level=9)
    return output.getvalue()


def convert(payload):
    with Image.open(io.BytesIO(payload)) as source:
        original_size = source.size
        has_alpha = "A" in source.getbands() or "transparency" in source.info
        resized = source.convert("RGBA" if has_alpha else "RGB").resize(
            SIZE, Image.Resampling.LANCZOS
        )
    # Avoid carrying source metadata into the exported PNG.
    resized.info.clear()
    png = encode_png(resized)
    if len(png) < MAX_BYTES:
        return png, original_size, None
    for colors in (256, 128, 64, 32, 16, 8, 2):
        method = Image.Quantize.FASTOCTREE if has_alpha else Image.Quantize.MEDIANCUT
        reduced = resized.quantize(colors=colors, method=method,
                                   dither=Image.Dither.NONE)
        png = encode_png(reduced)
        if len(png) < MAX_BYTES:
            return png, original_size, colors
    raise ValueError("Unable to produce a PNG below the size limit")


def export(source, output, archive=None):
    document, binary = read_glb(source)
    if not document.get("images"):
        raise ValueError("GLB contains no images")
    if output.exists():
        raise ValueError(f"Output already exists; choose a new directory: {output}")
    if archive is not None:
        if archive.exists():
            raise ValueError(f"ZIP already exists: {archive}")
        if archive.resolve().is_relative_to(output.resolve()):
            raise ValueError("ZIP path must be outside the output directory")
    output.parent.mkdir(parents=True, exist_ok=True)
    # Finish every conversion before publishing the output directory.
    with tempfile.TemporaryDirectory(prefix=".glb-textures-", dir=output.parent) as temp:
        staging = Path(temp) / "textures"
        staging.mkdir()
        results = []
        for index, name, payload in image_payloads(document, binary, source.parent):
            safe_name = re.sub(r"[^A-Za-z0-9_-]+", "_", str(name)).strip("_")[:80] or "image"
            filename = f"{index:03d}_{safe_name}.png"
            try:
                png, original_size, colors = convert(payload)
            except (OSError, ValueError) as error:
                raise ValueError(f"Image {index} ({name}): {error}. KTX2/BasisU requires prior decoding.") from error
            (staging / filename).write_bytes(png)
            results.append((filename, len(png), original_size, colors))
        staging.rename(output)
    if archive is not None:
        archive.parent.mkdir(parents=True, exist_ok=True)
        with zipfile.ZipFile(archive, "x", compression=zipfile.ZIP_DEFLATED) as zip_file:
            for filename, *_ in results:
                zip_file.write(output / filename, filename)
    return results


def batch_export(input_directory, output_directory):
    if not input_directory.is_dir():
        raise ValueError(f"Input directory does not exist: {input_directory}")
    sources = sorted(p for p in input_directory.iterdir()
                     if p.is_file() and p.suffix.lower() == ".glb")
    if not sources:
        raise ValueError(f"No GLB files found in {input_directory}")
    names = [p.stem.casefold() for p in sources]
    if len(names) != len(set(names)):
        raise ValueError("GLB filenames would produce duplicate ZIP names; rename them first")
    output_directory.mkdir(parents=True, exist_ok=True)
    failures = []
    for source in sources:
        archive = output_directory / (source.stem + ".zip")
        try:
            # Stage on the destination volume, then atomically replace a previous ZIP.
            with tempfile.TemporaryDirectory(prefix=".export-", dir=output_directory) as temp:
                staging = Path(temp)
                results = export(source, staging / "textures", staging / "textures.zip")
                (staging / "textures.zip").replace(archive)
            print(f"OK: {source.name} -> {archive.name} ({len(results)} images)", flush=True)
        except (OSError, ValueError, KeyError, TypeError, Image.DecompressionBombError) as error:
            failures.append(source.name)
            print(f"FAILED: {source.name}: {error}", file=sys.stderr, flush=True)
    print(f"Batch complete: {len(sources) - len(failures)} succeeded, {len(failures)} failed.", flush=True)
    return failures


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", type=Path, nargs="?", help="Input GLB file, or input directory with --batch")
    parser.add_argument("--batch", action="store_true", help="Export all GLBs directly in input to ZIPs in output")
    parser.add_argument("-o", "--output", type=Path, help="New output directory (default: <input>_textures)")
    parser.add_argument("--zip", nargs="?", const="", metavar="PATH",
                        help="Also create a ZIP (default: <output>.zip)")
    args = parser.parse_args()
    if args.batch:
        if args.zip is not None:
            parser.error("--batch always creates ZIPs; do not specify --zip")
        root = Path(__file__).resolve().parent
        try:
            failures = batch_export(args.input or root / "input", args.output or root / "output")
            return 1 if failures else 0
        except (OSError, ValueError) as error:
            print(f"Error: {error}", file=sys.stderr)
            return 1
    if args.input is None:
        parser.error("input is required unless --batch is specified")
    output = args.output or args.input.with_name(args.input.stem + "_textures")
    archive = (Path(args.zip) if args.zip else Path(str(output) + ".zip")) if args.zip is not None else None
    try:
        results = export(args.input, output, archive)
    except (OSError, ValueError, KeyError, TypeError, Image.DecompressionBombError) as error:
        print(f"Error: {error}", file=sys.stderr)
        return 1
    for name, size, original, colors in results:
        reduction = f", quantized to {colors} colors" if colors else ""
        print(f"{name}: {original[0]}x{original[1]} -> 1024x1024, {size:,} bytes{reduction}")
    print(f"Exported {len(results)} images to {output}")
    if archive:
        print(f"ZIP: {archive} ({archive.stat().st_size:,} bytes)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
