"""UVEX 4i long-slit spectroscopy reduction pipeline."""

from .models import FrameRecord, FrameType, ReductionResult
from .pipeline import ReductionPipeline
from .products import read_spectrum
from .workflow import run_full_workflow

__all__ = [
    "FrameRecord",
    "FrameType",
    "ReductionPipeline",
    "ReductionResult",
    "read_spectrum",
    "run_full_workflow",
]
__version__ = "0.4.1"
