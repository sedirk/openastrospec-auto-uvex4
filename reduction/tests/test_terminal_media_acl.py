"""Exercise the exact Windows DACL maintenance helper using a private sandbox."""
import csv
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
import uuid

import pytest


@pytest.mark.skipif(os.name != "nt", reason="Windows file ACLs")
def test_exact_delete_ace_no_inheritance_no_photometry_or_digest_bypass(tmp_path):
    script = Path(__file__).resolve().parents[2] / "scripts/grant-qhy-terminal-file-delete.ps1"
    sid = next(csv.reader(io.StringIO(subprocess.check_output(
        ["whoami", "/user", "/fo", "csv", "/nh"], text=True))))[1]
    with tempfile.TemporaryDirectory(prefix="uvex-acl-parent-") as outside:
        # Helper enforces an immediate child of LOCAL TEMP of a fixed shape.
        root = Path(tempfile.gettempdir()) / ("uvex-acl-sim-" + uuid.uuid4().hex)
        raw = root / "UVEX-20261119T163053Z-abcd" / ("qhy-" + uuid.uuid4().hex) / "raw"
        raw.mkdir(parents=True)
        name = "000001_ACQUISITION-WIDE-STREAM_20261119T163516.1234567Z_" + uuid.uuid4().hex + ".fits"
        image = raw / name
        second = raw / name.replace("000001_", "000002_")
        science = raw / name.replace("ACQUISITION-WIDE-STREAM", "PHOTOMETRY")
        sentinel = Path(outside) / name
        for path in [image, second, science, sentinel]:
            path.write_bytes(b"synthetic pixels only")
        def invoke(path, *, bad=False):
            data = path.read_bytes()
            listing = tmp_path / "list.json"
            listing.write_text(json.dumps([dict(path=str(p),size=len(data),
                sha256=hashlib.sha256(data).hexdigest()) for p in (path,second)]),encoding="utf-8")
            return subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "RemoteSigned", "-File", str(script),
                "-List",str(listing),"-Digest","0"*64 if bad else hashlib.sha256(listing.read_bytes()).hexdigest(),
                "-Sid",sid,"-SimulationRoot",str(root)],capture_output=True)
        def acl(path):
            return subprocess.check_output(["icacls.exe",str(path)])
        try:
            protected = {p:acl(p) for p in [raw,science,sentinel]}
            initially = acl(image)
            assert invoke(image,bad=True).returncode != 0 and acl(image) == initially
            assert invoke(science).returncode != 0 and invoke(sentinel).returncode != 0
            proc = invoke(image)
            assert proc.returncode == 0, proc.stderr
            assert all(b"(D)" in acl(p) and p.read_bytes() == b"synthetic pixels only" for p in (image,second))
            assert all(acl(p) == state for p,state in protected.items())
        finally:
            for p in (image,second,science):
                p.unlink(missing_ok=True)
            for p in (raw,raw.parent,raw.parent.parent,root):
                p.rmdir()
