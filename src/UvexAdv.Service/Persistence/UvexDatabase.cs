using System.Text.Json;
using Microsoft.Data.Sqlite;
using UvexAdv.Core;
using UvexAdv.Service.Operations;

namespace UvexAdv.Service.Persistence;

public enum CalibrationProfileScope
{
    Unspecified,
    Simulator,
    Hardware,
}

public sealed record CalibrationProfile(
    string Id,
    string Name,
    int GratingLinesPerMm,
    int SlitPosition,
    int Binning,
    int RoiX,
    int RoiY,
    int RoiWidth,
    int RoiHeight,
    string DispersionAxis,
    double GratingStepsPerPixel,
    double[] WavelengthCoefficients,
    double[] ReferenceLineWavelengthsNm,
    DateTimeOffset UpdatedUtc,
    CalibrationProfileScope Scope = CalibrationProfileScope.Unspecified,
    string? HardwareBinding = null);

public static class CalibrationProfilePolicy
{
    public static IReadOnlyList<CalibrationProfile> CompatibleProfiles(
        IEnumerable<CalibrationProfile> profiles,
        UvexSafetyOptions options) =>
        profiles.Where(profile => IsCompatible(profile, options)).ToArray();

    public static bool IsCompatible(CalibrationProfile profile, UvexSafetyOptions options)
    {
        if (options.Simulator)
        {
            // `sim-default` predates the explicit Scope field. It remains available
            // only to the simulator so the migration cannot turn it into real data.
            return profile.Scope == CalibrationProfileScope.Simulator ||
                   (profile.Scope == CalibrationProfileScope.Unspecified &&
                    profile.Id.StartsWith("sim-", StringComparison.OrdinalIgnoreCase));
        }

        return profile.Scope == CalibrationProfileScope.Hardware &&
               profile.GratingLinesPerMm == options.ExpectedGratingLinesPerMm &&
               string.Equals(
                   profile.HardwareBinding,
                   ExpectedHardwareBinding(options),
                   StringComparison.OrdinalIgnoreCase);
    }

    public static CalibrationProfile PrepareForStorage(
        CalibrationProfile profile,
        UvexSafetyOptions options)
    {
        if (options.Simulator)
        {
            if (profile.Scope != CalibrationProfileScope.Simulator)
            {
                throw new InvalidOperationException(
                    "A simulator service only accepts calibration profiles explicitly scoped to Simulator.");
            }

            return profile with { HardwareBinding = null };
        }

        if (profile.Scope != CalibrationProfileScope.Hardware)
        {
            throw new InvalidOperationException(
                "A real UVEX service only accepts calibration profiles explicitly scoped to Hardware.");
        }

        if (profile.GratingLinesPerMm != options.ExpectedGratingLinesPerMm)
        {
            throw new InvalidOperationException(
                $"Calibration grating is {profile.GratingLinesPerMm} lines/mm; this installation expects {options.ExpectedGratingLinesPerMm} lines/mm.");
        }

        return profile with { HardwareBinding = ExpectedHardwareBinding(options) };
    }

    public static string ExpectedHardwareBinding(UvexSafetyOptions options) =>
        $"{options.PortName.ToUpperInvariant()}|VID_{options.ExpectedUsbVid.ToUpperInvariant()}|PID_{options.ExpectedUsbPid.ToUpperInvariant()}";
}

public sealed class UvexDatabase
{
    private readonly object gate = new();
    private readonly string connectionString;

    public UvexDatabase(UvexDataPaths paths)
    {
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.Database,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
        Initialize();
    }

    public void UpsertOperation(UvexOperation operation)
    {
        lock (gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO operations (id, kind, state, started_utc, completed_utc, error)
                VALUES ($id, $kind, $state, $started, $completed, $error)
                ON CONFLICT(id) DO UPDATE SET
                    state=excluded.state, completed_utc=excluded.completed_utc, error=excluded.error;
                """;
            command.Parameters.AddWithValue("$id", operation.Id.ToString("D"));
            command.Parameters.AddWithValue("$kind", operation.Kind);
            command.Parameters.AddWithValue("$state", operation.State.ToString());
            command.Parameters.AddWithValue("$started", operation.StartedUtc.ToString("O"));
            command.Parameters.AddWithValue("$completed", operation.CompletedUtc?.ToString("O") ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$error", operation.Error ?? (object)DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    public UvexDeviceStatus? GetLastDeviceStatus()
    {
        lock (gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload_json FROM device_state WHERE id = 1";
            return command.ExecuteScalar() is string payload
                ? JsonSerializer.Deserialize<UvexDeviceStatus>(payload)
                : null;
        }
    }

    public void UpsertDeviceStatus(UvexDeviceStatus status)
    {
        if (!status.PositionKnown || status.PositionTrust != UvexPositionTrust.Live)
        {
            return;
        }

        lock (gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO device_state (id, measured_utc, payload_json)
                VALUES (1, $measured, $payload)
                ON CONFLICT(id) DO UPDATE SET measured_utc=excluded.measured_utc, payload_json=excluded.payload_json;
                """;
            command.Parameters.AddWithValue("$measured", status.PositionMeasuredUtc?.ToString("O") ?? DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(status));
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<CalibrationProfile> GetCalibrationProfiles()
    {
        lock (gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload_json FROM calibration_profiles ORDER BY updated_utc DESC";
            using var reader = command.ExecuteReader();
            var profiles = new List<CalibrationProfile>();
            while (reader.Read())
            {
                if (JsonSerializer.Deserialize<CalibrationProfile>(reader.GetString(0)) is { } profile)
                {
                    profiles.Add(profile);
                }
            }

            return profiles;
        }
    }

    public CalibrationProfile? GetCalibrationProfile(string id) =>
        GetCalibrationProfiles().FirstOrDefault(profile => profile.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public void UpsertCalibrationProfile(CalibrationProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.Name))
        {
            throw new ArgumentException("Calibration profile Id and Name are required.");
        }

        var normalized = profile with { UpdatedUtc = DateTimeOffset.UtcNow };
        lock (gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO calibration_profiles (id, name, updated_utc, payload_json)
                VALUES ($id, $name, $updated, $payload)
                ON CONFLICT(id) DO UPDATE SET name=excluded.name, updated_utc=excluded.updated_utc, payload_json=excluded.payload_json;
                """;
            command.Parameters.AddWithValue("$id", normalized.Id);
            command.Parameters.AddWithValue("$name", normalized.Name);
            command.Parameters.AddWithValue("$updated", normalized.UpdatedUtc.ToString("O"));
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(normalized));
            command.ExecuteNonQuery();
        }
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS operations (
                id TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                state TEXT NOT NULL,
                started_utc TEXT NOT NULL,
                completed_utc TEXT NULL,
                error TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS calibration_profiles (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                payload_json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS device_state (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                measured_utc TEXT NOT NULL,
                payload_json TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
}
