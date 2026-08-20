namespace UvexAdv.Protocol;

/// <summary>
/// Safe command subset from UVEX4 Manager serial protocol v1.0 (2024-02-10).
/// EEPROM, network writes and firmware operations are deliberately absent.
/// </summary>
public static class UvexCommands
{
    public static UvexCommand Ping() => UvexCommand.Query("ISLV");
    public static UvexCommand InitializationComplete() => UvexCommand.Query("IIOK");
    public static UvexCommand Configuration() => UvexCommand.Query("IST0");
    public static UvexCommand FirmwareVersion() => UvexCommand.Query("IVE1");
    public static UvexCommand Description() => UvexCommand.Query("IDE1");
    public static UvexCommand Temperature() => UvexCommand.Query("ITEM");
    public static UvexCommand Busy() => UvexCommand.Query("IBSY");

    public static UvexCommand GratingPosition() => UvexCommand.Query("GPOS");
    public static UvexCommand GratingMinimum() => UvexCommand.Query("GMIN");
    public static UvexCommand GratingMaximum() => UvexCommand.Query("GMAX");
    public static UvexCommand GratingHome() => new("GGOO", [], CausesMotion: true);
    public static UvexCommand GratingMovePositive(int steps) => UvexCommand.WithInt("GGCC", steps, true);
    public static UvexCommand GratingMoveNegative(int steps) => UvexCommand.WithInt("GGCW", steps, true);
    public static UvexCommand GratingGotoWavelengthAngstrom(int wavelength) => UvexCommand.WithInt("GGTL", wavelength, true);
    public static UvexCommand GratingStop() => new("GSTP", [], IsEmergency: true);

    public static UvexCommand FocusPosition() => UvexCommand.Query("FPOS");
    public static UvexCommand FocusMaximum() => UvexCommand.Query("FMAX");
    public static UvexCommand FocusAbsoluteCapability() => UvexCommand.Query("FABS");
    public static UvexCommand FocusHome() => new("FHOM", [], CausesMotion: true);
    public static UvexCommand FocusIn(int steps) => UvexCommand.WithInt("FGIN", steps, true);
    public static UvexCommand FocusOut(int steps) => UvexCommand.WithInt("FGOU", steps, true);
    public static UvexCommand FocusStop() => new("FSTP", [], IsEmergency: true);

    public static UvexCommand SlitPosition() => UvexCommand.Query("SPOS");
    public static UvexCommand SlitMotorPosition() => UvexCommand.Query("STEP");
    public static UvexCommand SlitMaximum() => UvexCommand.Query("SMAX");
    public static UvexCommand SlitNames() => UvexCommand.Query("SNAM");
    public static UvexCommand SlitOffset(int position) =>
        new("SGOF", [position.ToString(System.Globalization.CultureInfo.InvariantCulture)], ExpectsResponse: true);
    public static UvexCommand SlitSetOffset(int position, int offsetSteps) =>
        new("SSOF",
            [
                position.ToString(System.Globalization.CultureInfo.InvariantCulture),
                offsetSteps.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ]);
    public static UvexCommand SlitPhotodiodeValue() => UvexCommand.Query("SINT");
    public static UvexCommand SlitPhotodiodeThreshold() => UvexCommand.Query("SGTS");
    public static UvexCommand SlitPhotodiodeEnabled() => UvexCommand.Query("SGPH");
    public static UvexCommand SlitIlluminationOn() => new("SLON", []);
    public static UvexCommand SlitIlluminationOff() => new("SLOF", []);
    public static UvexCommand SlitMove(int position, bool usePhotodiode) =>
        new(
            "SMOV",
            [position.ToString(System.Globalization.CultureInfo.InvariantCulture), usePhotodiode ? "1" : "0"],
            CausesMotion: true);
    public static UvexCommand SlitCalibratePosition(int position) => UvexCommand.WithInt("SPS0", position);
    public static UvexCommand SlitAutoCalibratePhotodiode() => new("SPAC", []);
    public static UvexCommand SlitStop() => new("SSTP", [], IsEmergency: true);

    public static UvexCommand CalibrationRelayCount() => UvexCommand.Query("CMAX");
    public static UvexCommand CalibrationRelayNames() => UvexCommand.Query("CNAM");
    public static UvexCommand CalibrationRelayStates() => UvexCommand.Query("CGAC");
    public static UvexCommand CalibrationRelay(int relay, bool enabled) =>
        new("CACT", [relay.ToString(System.Globalization.CultureInfo.InvariantCulture), enabled ? "1" : "0"]);
    public static UvexCommand CalibrationClear() => new("CCLR", []);
}
