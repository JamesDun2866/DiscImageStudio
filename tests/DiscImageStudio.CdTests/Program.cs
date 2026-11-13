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

GoldenOutputTests.Run();

Console.WriteLine("cd-tests: all checks passed");
