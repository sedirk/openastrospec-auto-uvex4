"""Opt-in end-to-end protocol test using the real C# client, never hardware."""
import json
import os
from pathlib import Path
import subprocess
import sys
import numpy as np
import pytest
from test_detector import shape_frame


def test_real_csharp_python_worker_roundtrip():
    dotnet = os.environ.get("UVEX_SEP_TEST_DOTNET")
    dll = os.environ.get("UVEX_SEP_TEST_DLL")
    if not dotnet or not dll:
        pytest.skip("Set UVEX_SEP_TEST_DOTNET and UVEX_SEP_TEST_DLL for real C# transport test")
    image, truth = shape_frame("donut")
    raw = np.clip(np.rint(image), 0, 65535).astype("<u2")
    header = json.dumps({"width":192,"height":192}) + "\n"
    call = subprocess.run([dotnet,dll,sys.executable,str(Path(__file__).with_name("worker.py").resolve())],
        input=header.encode()+raw.tobytes(),capture_output=True,timeout=65,check=True)
    result=json.loads(call.stdout)
    assert not result["MotionAuthorized"] and not result["TargetIdentityConfirmed"]
    star=result["Sources"][0]
    assert np.hypot(star["X"]-truth[0],star["Y"]-truth[1]) < .5
    assert star["FocusEligible"]
    assert "children" in star


def test_real_csharp_focus_worker_tracks_same_irregular_stars():
    dotnet, dll = os.environ.get('UVEX_SEP_TEST_DOTNET'), os.environ.get('UVEX_SEP_TEST_DLL')
    if not dotnet or not dll:
        pytest.skip('Set C# transport environment')
    y, x = np.mgrid[:360, :360]
    reference = None
    for offset in [0, 9]:
        image = 4100 + np.random.default_rng(0).normal(0, 5, x.shape)
        for xx, yy in [(70,70),(260,80),(160,265)]:
            image += 3000*np.exp(-((x-xx-offset)**2/18+(y-yy)**2/40))
        raw = np.rint(image).astype('<u2')
        header = dict(width=360, height=360)
        if reference is not None:
            header['reference'] = reference
        call = subprocess.run([dotnet,dll,'focus',sys.executable,str(Path(__file__).with_name('worker.py').resolve())],
            input=(json.dumps(header)+'\n').encode()+raw.tobytes(),capture_output=True,check=True,timeout=65)
        result = json.loads(call.stdout)
        assert len(result['Stars']) == 3
        assert all(s['R50'] > 0 and s['R80'] >= s['R50'] for s in result['Stars'])
        reference = result['Reference']
