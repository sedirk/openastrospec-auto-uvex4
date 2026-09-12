"""One bounded stdin/stdout request; no device, socket or observation-file APIs."""
import json
import sys
import numpy as np
from sep_detector import MAX_PIXELS, Settings, detect


def run(stream, output):
    line = stream.readline(4097)
    if len(line) > 4096 or not line.endswith(b"\n"):
        raise ValueError("Invalid request header.")
    header = json.loads(line)
    width, height = int(header["width"]), int(header["height"])
    if header.get("schema") != 1 or min(width, height) < 16 or width * height > MAX_PIXELS:
        raise ValueError("Invalid request dimensions/schema.")
    count = width * height * 2
    payload = stream.read(count)
    if len(payload) != count:
        raise ValueError("Truncated uint16 image.")
    if stream.read(1):
        raise ValueError("Unexpected trailing data.")
    image = np.frombuffer(payload, dtype="<u2").reshape(height, width)
    result = detect(image, Settings(saturation=float(header["saturation"])))
    if header.get('focus_request') is not None:
        from focus_metrics import measure
        request = header['focus_request']
        reference = request.get('reference')
        if reference is not None and (not isinstance(reference, list) or not 3 <= len(reference) <= 30
                                      or any(not (0 <= s['x'] < width and 0 <= s['y'] < height) for s in reference)):
            raise ValueError('Invalid focus reference')
        try:
            focus = measure(image, reference, saturation=float(header['saturation']))
            result['focus'] = {k: focus[k] for k in ['shift', 'stars', 'algorithm']}
            result['focus']['reference'] = [dict(x=s['x'], y=s['y']) for s in focus['reference']]
        except ValueError as exc:
            result['focus'] = dict(reference=reference or [], stars=[], error=str(exc))
    output.write(json.dumps(result, allow_nan=False, separators=(",", ":")) + "\n")


if __name__ == "__main__":
    try:
        run(sys.stdin.buffer, sys.stdout)
    except Exception as exc:
        print(f"SEP measurement failed: {type(exc).__name__}: {exc}", file=sys.stderr)
        sys.exit(2)
