using System.Globalization;
using System.Text.Json;
using UvexAdv.Phd2;

namespace UvexAdv.Commissioning.Tool;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var command = CommandLine.Parse(args);
            return command.Area switch
            {
                "phd2" => await RunPhd2Async(command).ConfigureAwait(false),
                "night-setup" => await RunNightSetupAsync(command).ConfigureAwait(false),
                "commissioning" => await RunCommissioningAsync(command).ConfigureAwait(false),
                _ => throw new CommandLineException(Usage),
            };
        }
        catch (CommandLineException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Commissioning evidence command failed: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunPhd2Async(CommandLine command)
    {
        var requirement = new Phd2ProfileBindingRequirement(
            command.RequiredInt("profile-id"),
            command.Required("profile-name"),
            command.Required("camera-name"),
            command.Required("camera-stable-id"),
            command.Required("mount-name"),
            command.RequiredInt("binning"),
            command.RequiredInt("gain-percent"));
        if (command.Action == "export")
        {
            command.EnsureOnly("profile-id", "profile-name", "camera-name", "camera-stable-id", "mount-name", "binning", "gain-percent", "output");
            var output = command.OutputOrDefault("phd2-profile");
            var result = await EvidenceBuilders.ExportPhd2Async(requirement, output, overwrite: false).ConfigureAwait(false);
            WriteJson(result.Bindings);
            return 0;
        }
        if (command.Action == "validate")
        {
            command.EnsureOnly("profile-id", "profile-name", "camera-name", "camera-stable-id", "mount-name", "binning", "gain-percent", "input", "file-sha", "profile-sha");
            var result = await EvidenceBuilders.ValidatePhd2Async(
                requirement,
                command.Required("input"),
                command.RequiredHash("file-sha"),
                command.RequiredHash("profile-sha")).ConfigureAwait(false);
            WriteJson(result);
            return result.Valid ? 0 : 3;
        }
        throw new CommandLineException("PHD2 action must be 'export' or 'validate'." + Environment.NewLine + Usage);
    }

    private static async Task<int> RunNightSetupAsync(CommandLine command)
    {
        if (command.Action == "create")
        {
            command.EnsureOnly("definition", "definition-sha", "output");
            var result = await EvidenceBuilders.CreateNightSetupAsync(
                command.Required("definition"),
                command.RequiredHash("definition-sha"),
                command.OutputOrDefault("night-setup"),
                overwrite: false).ConfigureAwait(false);
            WriteJson(result.Bindings);
            return 0;
        }
        if (command.Action == "validate")
        {
            command.EnsureOnly("input", "sha");
            var result = await EvidenceBuilders.ValidateNightSetupAsync(
                command.Required("input"),
                command.RequiredHash("sha")).ConfigureAwait(false);
            WriteJson(result);
            return result.Valid ? 0 : 3;
        }
        throw new CommandLineException("Night Setup action must be 'create' or 'validate'." + Environment.NewLine + Usage);
    }

    private static async Task<int> RunCommissioningAsync(CommandLine command)
    {
        var inputs = new CommissioningInputFiles(
            command.Required("definition"),
            command.RequiredHash("definition-sha"),
            command.Required("night-setup"),
            command.RequiredHash("night-setup-sha"),
            command.Required("phd2-evidence"),
            command.RequiredHash("phd2-evidence-file-sha"),
            command.RequiredHash("phd2-profile-sha"));
        if (command.Action == "create")
        {
            command.EnsureOnly("definition", "definition-sha", "night-setup", "night-setup-sha", "phd2-evidence", "phd2-evidence-file-sha", "phd2-profile-sha", "output");
            var result = await EvidenceBuilders.CreateCommissioningPresetAsync(
                inputs,
                command.OutputOrDefault("commissioning-preset"),
                overwrite: false).ConfigureAwait(false);
            WriteJson(result.Bindings);
            return 0;
        }
        if (command.Action == "validate")
        {
            command.EnsureOnly("definition", "definition-sha", "night-setup", "night-setup-sha", "phd2-evidence", "phd2-evidence-file-sha", "phd2-profile-sha", "input", "sha");
            var result = await EvidenceBuilders.ValidateCommissioningPresetAsync(
                inputs,
                command.Required("input"),
                command.RequiredHash("sha")).ConfigureAwait(false);
            WriteJson(result);
            return result.Valid ? 0 : 3;
        }
        throw new CommandLineException("Commissioning action must be 'create' or 'validate'." + Environment.NewLine + Usage);
    }

    private static void WriteJson<T>(T value) =>
        Console.WriteLine(JsonSerializer.Serialize(value, ArtifactIO.JsonOptions));

    private const string Usage = """
UVEX-ADV commissioning evidence tool (read-only with respect to all devices)

  phd2 export --profile-id N --profile-name NAME --camera-name NAME
      --camera-stable-id ID --mount-name NAME --binning N --gain-percent N [--output FILE]

  phd2 validate <same profile arguments> --input FILE --file-sha SHA256 --profile-sha SHA256

  night-setup create --definition FILE --definition-sha SHA256 [--output FILE]
  night-setup validate --input FILE --sha SHA256

  commissioning create --definition FILE --definition-sha SHA256
      --night-setup FILE --night-setup-sha SHA256
      --phd2-evidence FILE --phd2-evidence-file-sha SHA256 --phd2-profile-sha SHA256
      [--output FILE]

  commissioning validate <same referenced inputs> --input PRESET --sha SHA256

If --output is omitted, a new timestamped file is written under
%ProgramData%\UVEX-ADV\commissioning. Existing evidence is never overwritten.
No command opens or controls PHD2,
a camera, the mount, COM5, the roof, or any other observatory device.
""";

    private sealed class CommandLine
    {
        private readonly Dictionary<string, string?> values;

        private CommandLine(string area, string action, Dictionary<string, string?> values)
        {
            Area = area;
            Action = action;
            this.values = values;
        }

        public string Area { get; }
        public string Action { get; }

        public static CommandLine Parse(IReadOnlyList<string> args)
        {
            if (args.Count < 2 || args[0] is "-h" or "--help" || args[1] is "-h" or "--help") throw new CommandLineException(Usage);
            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var index = 2; index < args.Count; index++)
            {
                var token = args[index];
                if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length <= 2) throw new CommandLineException($"Unexpected argument '{token}'.");
                var key = token[2..];
                if (values.ContainsKey(key)) throw new CommandLineException($"Argument '--{key}' was supplied more than once.");
                if (index + 1 < args.Count && !args[index + 1].StartsWith("--", StringComparison.Ordinal)) values[key] = args[++index];
                else values[key] = null;
            }
            return new CommandLine(args[0].ToLowerInvariant(), args[1].ToLowerInvariant(), values);
        }

        public string Required(string name)
        {
            if (!values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value)) throw new CommandLineException($"Required argument '--{name}' is missing.");
            return value;
        }

        public string RequiredHash(string name)
        {
            var value = ArtifactIO.NormalizeHash(Required(name));
            if (!ArtifactIO.IsSha256(value)) throw new CommandLineException($"Argument '--{name}' must be an explicit 64-character SHA-256.");
            return value;
        }

        public int RequiredInt(string name)
        {
            var text = Required(name);
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) throw new CommandLineException($"Argument '--{name}' must be an integer.");
            return value;
        }

        public bool Flag(string name)
        {
            if (!values.TryGetValue(name, out var value)) return false;
            if (value is not null) throw new CommandLineException($"Flag '--{name}' does not take a value.");
            return true;
        }

        public void EnsureOnly(params string[] allowed)
        {
            var allowedSet = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
            var unknown = values.Keys.Where(key => !allowedSet.Contains(key)).OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray();
            if (unknown.Length > 0) throw new CommandLineException($"Unknown argument(s): {string.Join(", ", unknown.Select(key => "--" + key))}.");
        }

        public string OutputOrDefault(string stem)
        {
            if (values.TryGetValue("output", out var value))
            {
                if (string.IsNullOrWhiteSpace(value)) throw new CommandLineException("Argument '--output' requires a path.");
                return Path.GetFullPath(value);
            }
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(programData)) throw new CommandLineException("ProgramData could not be resolved; supply --output explicitly.");
            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture);
            return Path.Combine(programData, "UVEX-ADV", "commissioning", $"{stem}-{timestamp}.json");
        }
    }

    private sealed class CommandLineException(string message) : Exception(message);
}
