// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using Wol.Games.Vagrant.Runtime;

var assembly = typeof(VibrationDriver).Assembly;
if (assembly == typeof(Program).Assembly) throw new InvalidOperationException("Gameplay was compiled into the test host.");
foreach (var dependency in assembly.GetReferencedAssemblies())
    if (dependency.Name is not { } name || !name.StartsWith("System", StringComparison.Ordinal) && name != "WolEngine.Core")
        throw new InvalidOperationException("Gameplay depends on a host backend: " + dependency.FullName);

foreach (var dependency in typeof(Wol.Engine.Imaging.RasterImage).Assembly.GetReferencedAssemblies())
    if (dependency.Name is not { } name || !name.StartsWith("System", StringComparison.Ordinal))
        throw new InvalidOperationException("The portable core depends on a backend: " + dependency.FullName);
ContentContracts.Check();

bool wasEnabled = VibrationDriver.Enabled;
try
{
    VibrationDriver.Enabled = true;
    var sent = new List<(float Weak, float Strong)>();
    var driver = new VibrationDriver((weak, strong) => sent.Add((weak, strong)));
    byte[] input = { 5, 2, 1, 0, 0, 2, 100, 2, 200, 0, 0, 99 };
    var envelope = VibrationDriver.Decode(input);
    if (envelope.Length != 11 || input.Length != 12) throw new InvalidOperationException("Track extent or source ownership changed.");
    driver.Start(envelope, 1);
    driver.Start(envelope, 2);
    driver.Start(envelope, 3);
    driver.Step();
    if (driver.Levels != (2, 255) || sent.Count != 1 || sent[0] != (1f, 1f))
        throw new InvalidOperationException("Two channels did not saturate or an occupied channel was replaced.");
    driver.Stop(2);
    if (sent.Count != 1) throw new InvalidOperationException("Tagged stop changed output before the next gameplay tick.");
    driver.Step();
    if (driver.Levels != (1, 200) || sent[^1] != (1f, 200 / 255f))
        throw new InvalidOperationException("Tagged stop removed the wrong channel or changed large-motor interpolation.");
    driver.Step();
    if (driver.Levels != (0, 100) || sent[^1] != (0f, 100 / 255f))
        throw new InvalidOperationException("The small motor did not stop independently of the large-motor ramp.");
    driver.Step();
    if (driver.Levels != (0, 0) || sent[^1] != (0f, 0f)) throw new InvalidOperationException("The motors did not finish at zero.");
    int endedCount = sent.Count;
    driver.Step(); driver.Stop();
    if (sent.Count != endedCount) throw new InvalidOperationException("Unchanged zero output was sent again.");

    sent.Clear();
    byte[] hold = { 5, 8, 1, 0, 0, 8, 255, 8, 255, 0, 0 };
    driver.Start(hold, 7); driver.Step();
    VibrationDriver.Enabled = false; driver.Step();
    if (driver.Levels != (1, 255) || sent.Count != 2 || sent[^1] != (0f, 0f))
        throw new InvalidOperationException("Disabling output changed the envelope state or failed to stop motors.");
    driver.Step();
    if (sent.Count != 2) throw new InvalidOperationException("Disabled output was resent without a strength change.");
    VibrationDriver.Enabled = true; driver.Step();
    if (sent.Count != 3 || sent[^1] != (1f, 1f)) throw new InvalidOperationException("Re-enabling output did not resume the current envelope.");
    driver.Stop();
    if (sent.Count != 4 || sent[^1] != (0f, 0f)) throw new InvalidOperationException("Stop-all did not stop immediately.");

    foreach (byte[] truncated in new[] { Array.Empty<byte>(), new byte[] { 0 }, new byte[] { 5, 2, 1 } })
    {
        bool rejected = false;
        try { _ = VibrationDriver.Decode(truncated); }
        catch (InvalidDataException) { rejected = true; }
        if (!rejected) throw new InvalidOperationException("A truncated envelope was accepted.");
    }
    Console.WriteLine("GAMEPLAY_CONTRACTS_OK: no Godot/backend references; two channels, saturation, independent tracks, tagged stops, output suppression and enable/resume");
}
finally { VibrationDriver.Enabled = wasEnabled; }
