import base64
import io
import json
from pathlib import Path
import random
import struct
import tempfile
import unittest
import zipfile

from PIL import Image

from extract_glb_textures import MAX_BYTES, batch_export, convert, export, read_glb, read_uri


def make_glb(document, binary=b""):
    metadata = json.dumps(document).encode()
    metadata += b" " * (-len(metadata) % 4)
    binary += b"\0" * (-len(binary) % 4)
    chunks = struct.pack("<II", len(metadata), 0x4E4F534A) + metadata
    if binary:
        chunks += struct.pack("<II", len(binary), 0x004E4942) + binary
    return struct.pack("<4sII", b"glTF", 2, 12 + len(chunks)) + chunks


class ExportTests(unittest.TestCase):
    def test_batch_continues_on_failure_and_replaces_only_successful_zip(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            inputs, outputs = root / "input", root / "output"
            inputs.mkdir()
            outputs.mkdir()
            image = io.BytesIO()
            Image.new("RGB", (4, 4), "red").save(image, "PNG")
            uri = "data:image/png;base64," + base64.b64encode(image.getvalue()).decode()
            for name in ("one.glb", "two.GLB"):
                (inputs / name).write_bytes(make_glb({"images": [{"uri": uri}]}))
            (inputs / "bad.glb").write_bytes(b"broken")
            (outputs / "bad.zip").write_bytes(b"previous export")
            (outputs / "one.zip").write_bytes(b"old")
            self.assertEqual(batch_export(inputs, outputs), ["bad.glb"])
            self.assertEqual((outputs / "bad.zip").read_bytes(), b"previous export")
            self.assertEqual({p.name for p in outputs.iterdir()}, {"one.zip", "two.zip", "bad.zip"})
            for name in ("one.zip", "two.zip"):
                with zipfile.ZipFile(outputs / name) as archive:
                    self.assertEqual(len(archive.namelist()), 1)
                    self.assertIsNone(archive.testzip())

    def test_embedded_data_uri_zip_and_alpha(self):
        image = io.BytesIO()
        Image.new("RGBA", (16, 8), (20, 40, 60, 0)).save(image, "PNG")
        payload = image.getvalue()
        document = {
            "asset": {"version": "2.0"},
            "buffers": [{"byteLength": len(payload)}],
            "bufferViews": [{"buffer": 0, "byteLength": len(payload)}],
            "images": [
                {"name": "../same", "bufferView": 0},
                {"name": "../same", "uri": "data:image/png;base64," + base64.b64encode(payload).decode()},
            ],
        }
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            source = root / "model.glb"
            source.write_bytes(make_glb(document, payload))
            output, archive = root / "out", root / "out.zip"
            results = export(source, output, archive)
            self.assertEqual(len(results), 2)
            with zipfile.ZipFile(archive) as zipped:
                self.assertEqual(set(zipped.namelist()), {r[0] for r in results})
                self.assertIsNone(zipped.testzip())
                for name, *_ in results:
                    data = (output / name).read_bytes()
                    self.assertEqual(zipped.read(name), data)
                    self.assertLess(len(data), MAX_BYTES)
                    with Image.open(io.BytesIO(data)) as result:
                        self.assertEqual(result.size, (1024, 1024))
                        self.assertEqual(result.convert("RGBA").getpixel((0, 0))[3], 0)
            with self.assertRaisesRegex(ValueError, "already exists"):
                export(source, output)

    def test_noisy_rgba_is_reduced_below_limit(self):
        image = Image.frombytes("RGBA", (1024, 1024), random.Random(42).randbytes(1024 * 1024 * 4))
        source = io.BytesIO()
        image.save(source, "PNG")
        self.assertGreater(len(source.getvalue()), MAX_BYTES)
        payload, _, colors = convert(source.getvalue())
        self.assertIsNotNone(colors)
        self.assertLess(len(payload), MAX_BYTES)
        with Image.open(io.BytesIO(payload)) as result:
            self.assertEqual(result.size, (1024, 1024))
            self.assertEqual(result.format, "PNG")
            self.assertLess(result.convert("RGBA").getextrema()[3][0], 255)

    def test_bad_input_leaves_no_output(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            source = root / "bad.glb"
            source.write_bytes(make_glb({"images": [{"uri": "data:image/png;base64,YmFk"}]}))
            with self.assertRaises(ValueError):
                export(source, root / "out")
            self.assertFalse((root / "out").exists())
            source.write_bytes(source.read_bytes()[:-1])
            with self.assertRaises(ValueError):
                read_glb(source)

    def test_local_resources_and_path_escape(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "some image.png").write_bytes(b"image")
            self.assertEqual(read_uri("some%20image.png", root), b"image")
            for uri in ("../outside.png", "https://example.com/image.png"):
                with self.assertRaises(ValueError):
                    read_uri(uri, root)


if __name__ == "__main__":
    unittest.main()
