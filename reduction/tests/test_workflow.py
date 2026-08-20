from uvex_reduce.workflow import StandardQuality, _accept_flat_trial


def _quality(correlation: float, scatter: float) -> StandardQuality:
    return StandardQuality(
        usable=True,
        template_correlation=correlation,
        response_fractional_scatter=scatter,
        matched_line_count=3,
        wavelength_rms_angstrom=None,
        reason="Passed.",
    )


def test_flat_trial_is_rejected_when_template_agreement_degrades() -> None:
    accepted, reason = _accept_flat_trial(_quality(0.82, 0.016), _quality(0.70, 0.020))

    assert accepted is False
    assert "correlation decreased" in reason


def test_flat_trial_is_rejected_when_response_becomes_rough() -> None:
    accepted, reason = _accept_flat_trial(_quality(0.80, 0.010), _quality(0.80, 0.030))

    assert accepted is False
    assert "fractional scatter increased" in reason


def test_flat_trial_is_accepted_only_after_both_quality_gates() -> None:
    accepted, reason = _accept_flat_trial(_quality(0.80, 0.020), _quality(0.79, 0.021))

    assert accepted is True
    assert "accepted" in reason
