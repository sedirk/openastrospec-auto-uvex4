"""Pure bounds checks; never construct the Windows lease or call an owner."""
import pytest
from commission_backlash import json_ready, validate_plan


def test_verified_measurement_envelope():
    validate_plan(5000, [('up', 4700), ('down', 5300), ('return', 5000)])


def test_missing_optional_diagnostics_are_null_not_zero():
    import json
    import numpy as np
    value = json_ready({'r50': 4.5, 'optional': [float('nan'), np.float64('inf')], 'n': np.int64(3)})
    assert value == {'r50': 4.5, 'optional': [None, None], 'n': 3}
    assert 'NaN' not in json.dumps(value, allow_nan=False)


@pytest.mark.parametrize('origin,positions', [
    (4999, [('up', 5000)]), (5000, []), (5000, [('up', 4699)]),
    (5000, [('up', 5301)]), (5000, [('up', 5000)]*41),
])
def test_unreviewed_origin_or_path_never_grants_motion(origin, positions):
    with pytest.raises(ValueError):
        validate_plan(origin, positions)
