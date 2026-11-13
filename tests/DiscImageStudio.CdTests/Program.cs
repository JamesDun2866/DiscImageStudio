// Cross-platform CD regression program. Unlike tests/DiscImageStudio.ArchitectureTests (WPF,
// Windows only) it runs on Linux/macOS too, so the CD generators and the channel model are checked
// in the CLI CI job and on non-Windows development machines.
//
//   dotnet run --project tests/DiscImageStudio.CdTests -c Release
//   dotnet run --project tests/DiscImageStudio.CdTests -c Release -- --print-golden

if (args is ["--print-golden"])
{
    GoldenOutputTests.Print();
    return;
}

if (args is ["--assess", string darkText, string lightText])
{
    // Developer aid: explain why a dark/light pair passes or fails the safety envelope.
    var assessment = DiscImageStudio.Cd.Physical.CdPatternSafety.AssessPair(
        DiscImageStudio.Cd.CdBytePattern.Parse(darkText), DiscImageStudio.Cd.CdBytePattern.Parse(lightText));
    Console.WriteLine($"envelope: {assessment.Envelope}");
    Console.WriteLine($"worst:    {assessment.Worst}");
    Console.WriteLine($"safe:     {assessment.Safe} {assessment.FailingScenario}");
    return;
}

if (args is ["--palette"])
{
    PaletteTests.Run();
    return;
}

if (args is ["--channel"])
{
    ChannelModelTests.Run();
    return;
}

GoldenOutputTests.Run();
ChannelModelTests.Run();
PaletteTests.Run();

Console.WriteLine("cd-tests: all checks passed");
