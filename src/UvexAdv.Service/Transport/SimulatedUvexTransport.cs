using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using UvexAdv.Protocol;

namespace UvexAdv.Service.Transport;

internal sealed class SimulatedUvexTransport : IUvexTransport
{
    private readonly Channel<string> responses = Channel.CreateUnbounded<string>();
    private int gratingSteps;
    private int focusSteps;
    private int slitPosition = 1;
    private int slitMotorSteps;
    private readonly string[] slitNames = ["300um", "15um", "25um", "35um"];
    private readonly int[] slitOffsets = new int[4];
    private bool slitIlluminationEnabled;

    public bool IsOpen { get; private set; }

    public Task OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsOpen = true;
        return Task.CompletedTask;
    }

    public Task CloseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsOpen = false;
        return Task.CompletedTask;
    }

    public async Task WriteAsync(string frame, CancellationToken cancellationToken)
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException("Simulator is closed.");
        }

        if (!UvexFrameParser.TryParse(frame, out var command))
        {
            await responses.Writer.WriteAsync(":IERR;400;#", cancellationToken).ConfigureAwait(false);
            return;
        }

        // The controller echoes each request before emitting its response.
        await responses.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);

        string? response = command.Code switch
        {
            "ISLV" => ":ISLV;#",
            "IIOK" => ":IIOK;#",
            "IVE1" => ":IVE1;2.3-simulator;#",
            "IDE1" => ":IDE1;UVEX4 simulator;#",
            "IST0" => ":IST0;167;#",
            "ITEM" => ":ITEM;21.50;#",
            "IBSY" => ":IBSY;1;#",
            "GPOS" => FormattableString.Invariant($":GPOS;{gratingSteps};{5500 + (gratingSteps / 10.0):0.0};3500;7500;#"),
            "GMIN" => ":GMIN;-250000;#",
            "GMAX" => ":GMAX;250000;#",
            "FPOS" => $":FPOS;{focusSteps};#",
            "FMAX" => ":FMAX;20000;#",
            "FABS" => ":FABS;0;#",
            "SPOS" => $":SPOS;{slitPosition};#",
            "STEP" => $":STEP;{slitMotorSteps};#",
            "SMAX" => ":SMAX;4;#",
            "SNAM" => $":SNAM;{string.Join(';', slitNames)};#",
            "SGOF" when command.TryGetInt32(0, out var offsetPosition) && offsetPosition is >= 1 and <= 4 =>
                $":SGOF;{offsetPosition};{slitOffsets[offsetPosition - 1]};#",
            "SINT" => slitIlluminationEnabled ? ":SINT;800;#" : ":SINT;29;#",
            "SGTS" => ":SGTS;283;#",
            "SGPH" => ":SGPH;1;#",
            "CMAX" => ":CMAX;0;#",
            _ => null,
        };

        if (IsMotion(command.Code))
        {
            // Match the real motion path: the controller emits unsolicited
            // IBSY transitions. Do not make the motion runner depend on an
            // IBSY query being serviced while the firmware is occupied.
            await responses.Writer.WriteAsync(":IBSY;0;#", cancellationToken).ConfigureAwait(false);
            ApplyMotion(command);
            await responses.Writer.WriteAsync(":IBSY;1;#", cancellationToken).ConfigureAwait(false);
            return;
        }

        ApplyMotion(command);
        if (response is not null)
        {
            await responses.Writer.WriteAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    public async IAsyncEnumerable<string> ReadChunksAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var response in responses.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return response;
        }
    }

    public ValueTask DisposeAsync()
    {
        IsOpen = false;
        responses.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private static bool IsMotion(string code) =>
        code is "GGOO" or "GGCC" or "GGCW" or "GGTL" or "FHOM" or "FGIN" or "FGOU" or "SMOV";

    private void ApplyMotion(UvexFrame command)
    {
        _ = command.TryGetInt32(0, out var first);
        switch (command.Code)
        {
            case "GGOO": gratingSteps = 0; break;
            case "GGCC": gratingSteps += Math.Abs(first); break;
            case "GGCW": gratingSteps -= Math.Abs(first); break;
            case "GGTL": gratingSteps = (first - 5500) * 10; break;
            case "FHOM": focusSteps = 0; break;
            case "FGIN": focusSteps += Math.Abs(first); break;
            case "FGOU": focusSteps -= Math.Abs(first); break;
            case "SMOV":
                slitPosition = Math.Clamp(first, 1, 4);
                slitMotorSteps = (slitPosition - 1) * 1000;
                break;
            case "SPS0": slitPosition = Math.Clamp(first, 1, 4); break;
            case "SLON": slitIlluminationEnabled = true; break;
            case "SLOF": slitIlluminationEnabled = false; break;
            case "SSOF" when command.TryGetInt32(1, out var offset):
                slitOffsets[Math.Clamp(first, 1, 4) - 1] = offset;
                break;
        }
    }
}
