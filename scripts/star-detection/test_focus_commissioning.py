"""No hardware: validate the commissioning envelope and readback interlocks."""
import pytest
import commission_focus as commissioning


def test_commissioning_positions_are_bounded_even_for_leave_point():
    commissioning.validate_positions([4900, 5000, 5100, 5000], 5000, 5050)
    for positions, origin, leave in [([], 5000, None), ([4500], 5000, None),
                                     ([5000], 5000, 5600), ([5000]*13, 5000, None),
                                     ([5401], 5000, None), ([5000], 7000, None)]:
        with pytest.raises(ValueError):
            commissioning.validate_positions(positions, origin, leave)


def valid_state():
    return dict(focuser=dict(Connected=True, DeviceId='ASCOM.StarFocuserPro.Focuser',
                            IsMoving=False, IsSettling=False, TempComp=False),
                mount=dict(Connected=True, Slewing=False, IsPulseGuiding=False,
                           TrackingEnabled=True, AtPark=False, Altitude=65, SideOfPier='pierEast'),
                flatdevice=dict(Connected=True, CoverState='Open', LightOn=False),
                dome=dict(Connected=True, ShutterStatus='ShutterOpen'),
                camera=dict(IsExposing=False), safetymonitor=dict(Connected=False, IsSafe=False))


@pytest.mark.parametrize('device,key,value', [
    ('focuser','DeviceId','other-focuser'), ('focuser','IsMoving',True),
    ('focuser','TempComp',True), ('mount','Slewing',True),
    ('mount','TrackingEnabled',False), ('mount','SideOfPier','pierWest'),
    ('mount','Altitude',20), ('flatdevice','CoverState','Closed'),
    ('flatdevice','LightOn',True), ('dome','ShutterStatus','ShutterClosed'),
    ('camera','IsExposing',True), ('safetymonitor','Connected',True)])
def test_unsafe_or_changed_owner_is_not_authorized(monkeypatch, device, key, value):
    states = valid_state()
    monkeypatch.setattr(commissioning, 'api', lambda path: states[path.split('/')[2]])
    monkeypatch.setattr(commissioning, 'phd', lambda method: 'Stopped')
    commissioning.physical('pierEast')
    states[device][key] = value
    with pytest.raises(RuntimeError):
        commissioning.physical('pierEast')


def test_guiding_is_never_stopped_to_take_a_focus_frame(monkeypatch):
    states = valid_state()
    monkeypatch.setattr(commissioning, 'api', lambda path: states[path.split('/')[2]])
    monkeypatch.setattr(commissioning, 'phd', lambda method: 'Guiding')
    with pytest.raises(RuntimeError, match='PHD2 must be stopped'):
        commissioning.physical('pierEast')
