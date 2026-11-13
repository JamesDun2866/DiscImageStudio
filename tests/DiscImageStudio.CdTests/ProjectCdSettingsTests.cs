using System.Text.Json;
using DiscImageStudio.Core;

/// <summary>The optional "cd" project section that stores custom disc bytes.</summary>
internal static class ProjectCdSettingsTests
{
    internal static void Run()
    {
        string work = Fixtures.CreateWorkDirectory("project-cd");
        try
        {
            ProjectDocument standard = Document(ProjectDocument.CdFamily, cd: null);
            string standardPath = Path.Combine(work, "standard.discproj");
            ProjectJsonStore.Save(standardPath, standard);
            using (JsonDocument json = JsonDocument.Parse(File.ReadAllText(standardPath)))
            {
                Check.True(!json.RootElement.TryGetProperty("cd", out _), "standard CD projects carry no cd section");
            }

            ProjectDocument custom = Document(ProjectDocument.CdFamily, new ProjectCdSettings(LightBytes: "F2-A8"));
            string customPath = Path.Combine(work, "custom.discproj");
            ProjectJsonStore.Save(customPath, custom);
            using (JsonDocument json = JsonDocument.Parse(File.ReadAllText(customPath)))
            {
                JsonElement section = json.RootElement.GetProperty("cd");
                Check.Equal("F2-A8", section.GetProperty("lightBytes").GetString(), "light bytes stored");
                Check.True(!section.TryGetProperty("darkBytes", out _), "standard dark bytes are omitted");
            }

            Check.Equal(new ProjectCdSettings(LightBytes: "F2-A8"), ProjectJsonStore.Load(customPath).Cd, "cd section round trip");

            Check.Throws<InvalidDataException>(
                () => ProjectJsonStore.Validate(Document(ProjectDocument.DvdFamily, new ProjectCdSettings(LightBytes: "B6"))),
                "cd section is CD-only");
            Check.Throws<InvalidDataException>(
                () => ProjectJsonStore.Validate(Document(ProjectDocument.CdFamily, new ProjectCdSettings(LightBytes: "XYZ"))),
                "invalid hex is refused");
            Check.Throws<InvalidDataException>(
                () => ProjectJsonStore.Validate(Document(ProjectDocument.CdFamily, new ProjectCdSettings("0D", "0d"))),
                "dark and light bytes must differ");
            ProjectJsonStore.Validate(Document(ProjectDocument.CdFamily, new ProjectCdSettings("15", "F2-40")));
            Console.WriteLine("project cd settings: passed");
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static ProjectDocument Document(string family, ProjectCdSettings? cd)
        => new(
            ProjectJsonStore.CurrentSchemaVersion,
            "Contrast tools",
            family,
            new ProjectGeometry(24.5, 56.8, 359_849),
            [],
            new ProjectOutputs("out.wav", true, string.Empty, "CD_IMAGE", true),
            null,
            null,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            ToneProfile.Photo,
            null,
            cd);
}
