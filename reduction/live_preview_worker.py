"""Bundled image-only worker entrypoint (also usable from the source checkout)."""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent / "src"))
from uvex_reduce.live_preview import main  # noqa: E402

main()
