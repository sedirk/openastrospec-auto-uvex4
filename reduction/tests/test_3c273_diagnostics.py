from reduction.tools.analyse_3c273 import (
    CATALOGUE_REDSHIFT,
    FeatureMeasurement,
    _identity_quality_gates,
)


def _candidate(name: str, offset: float, redshift: float) -> FeatureMeasurement:
    return FeatureMeasurement(
        name=name,
        rest_angstrom=5000.0,
        expected_angstrom=5800.0,
        measured_peak_angstrom=5800.0 + offset,
        offset_angstrom=offset,
        peak_normalized_flux=1.2,
        redshift=redshift,
        use_for_redshift=True,
        interpretation="test candidate",
    )


def test_identity_gate_rejects_inconsistent_window_maxima() -> None:
    measurements = [
        _candidate("H-gamma", -19.4, 0.1539),
        _candidate("H-beta", -69.3, 0.1441),
        _candidate("[O III]", -68.4, 0.1447),
    ]

    classification, gates, _ = _identity_quality_gates(
        measurements,
        measured_redshift=0.14467,
        reference_correlation=-0.016,
    )

    assert classification.startswith("unconfirmed-inconsistent")
    assert gates["medianRedshiftGatePassed"] is False
    assert gates["featureOffsetGatePassed"] is False
    assert gates["referenceCorrelationGatePassed"] is False


def test_identity_gate_allows_consistent_but_not_astrometric_label() -> None:
    measurements = [
        _candidate("H-gamma", 3.0, CATALOGUE_REDSHIFT + 0.001),
        _candidate("H-beta", -4.0, CATALOGUE_REDSHIFT - 0.001),
        _candidate("[O III]", 2.0, CATALOGUE_REDSHIFT),
    ]

    classification, gates, _ = _identity_quality_gates(
        measurements,
        measured_redshift=CATALOGUE_REDSHIFT,
        reference_correlation=0.30,
    )

    assert classification == "spectrally-consistent-with-3C273-not-astrometrically-proven"
    assert gates["medianRedshiftGatePassed"] is True
    assert gates["featureOffsetGatePassed"] is True
    assert gates["referenceCorrelationGatePassed"] is True
