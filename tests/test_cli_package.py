"""Smoke-test a published CLI from a working directory outside the checkout."""

import json
import shutil
import subprocess
import sys
import tempfile
import wave
from pathlib import Path


def write_song(path: Path, seconds: float) -> None:
    """A CD-format WAV (44.1 kHz, 16-bit, stereo) holding a quiet ramp."""
    frames = int(44100 * seconds)
    with wave.open(str(path), "wb") as song:
        song.setnchannels(2)
        song.setsampwidth(2)
        song.setframerate(44100)
        song.writeframes(bytes((index // 4) & 0x3F for index in range(frames * 4)))


def check_package(executable: Path) -> None:
    fixture = Path(__file__).resolve().parents[1] / "src/DiscImageStudio.App/Assets/DiscImageStudio-256.png"
    with tempfile.TemporaryDirectory() as directory:
        work = Path(directory)
        shutil.copyfile(fixture, work / "input image.png")

        def run(*args: str) -> None:
            subprocess.run([executable, *args], cwd=work, check=True)

        def check_size(name: str, expected: int) -> None:
            actual = (work / name).stat().st_size
            if actual != expected:
                raise ValueError(f"{name}: expected {expected} bytes, found {actual}")

        run("--help")
        run("selftest")
        run("cd-generate", "--input", "input image.png", "--output", "track.wav", "--sectors", "2")
        check_size("track.wav", 44 + 2 * 2352)
        if 'FILE "track.wav" WAVE' not in (work / "track.cue").read_text(encoding="utf-8-sig"):
            raise ValueError("CUE does not reference the generated WAV")

        run("cd-generate", "--input", "input image.png", "--output", "custom.wav", "--sectors", "40",
            "--palette-light", "F2-A8")
        sidecar = json.loads((work / "custom.wav.json").read_text(encoding="utf-8"))
        if sidecar.get("paletteLight") != "F2-A8":
            raise ValueError("cd-generate did not record the custom light bytes")
        refused = subprocess.run([executable, "cd-generate", "--input", "input image.png", "--output", "unvetted.wav",
                                  "--sectors", "40", "--palette-light", "F7-A8"], cwd=work)
        if refused.returncode == 0:
            raise ValueError("cd-generate accepted disc bytes outside the safety envelope")

        run("cd-contrast-test", "--output", "contrast.wav", "--sectors", "2")
        check_size("contrast.wav", 44 + 2 * 2352)
        legend = json.loads((work / "contrast.wav.contrast.json").read_text(encoding="utf-8"))
        if legend.get("format") != "cd-contrast-test-v1" or len(legend.get("predictions", [])) != 6:
            raise ValueError("cd-contrast-test did not write its legend")

        # Picture + music: two songs (3 s plus a 2 s gap = 375 sectors; 1 s padded to 300) after the
        # picture (2000 - 75 guard - 675 = 1250 sectors) hidden in track 1's pregap.
        write_song(work / "one.wav", 3)
        write_song(work / "two.wav", 1)
        (work / "songs.m3u8").write_text("#EXTM3U\n#EXTINF:3,Opening\none.wav\ntwo.wav\n", encoding="utf-8")
        run("cd-generate", "--input", "input image.png", "--output", "music.wav", "--sectors", "2000",
            "--music", "songs.m3u8")
        check_size("music.wav", 44 + 2000 * 2352)
        cue = (work / "music.cue").read_text(encoding="utf-8-sig")
        for line in ('TITLE "Opening"', "INDEX 00 00:00:00", "INDEX 01 00:17:50", "TRACK 02 AUDIO",
                     "INDEX 01 00:22:50"):
            if line not in cue:
                raise ValueError(f"music CUE is missing {line!r}:\n{cue}")
        music_sidecar = json.loads((work / "music.wav.json").read_text(encoding="utf-8"))
        if music_sidecar.get("musicPlacement") != "outer" or music_sidecar.get("pictureSectors") != 1250:
            raise ValueError(f"music sidecar does not describe the picture: {music_sidecar}")

        run("cd-channel-report", "--patterns", "0D,AA,F2-A8", "--candidates", "1", "--output", "channel.json")
        report = json.loads((work / "channel.json").read_text(encoding="utf-8"))
        if report.get("format") != "cd-channel-report-v1" or len(report.get("patterns", [])) != 3:
            raise ValueError("cd-channel-report did not describe the three requested patterns")

        run("solve", "--image", "input image.png", "--iso-output", "dvd.iso",
            "--total-sectors", "160", "--fill-sectors", "160")
        check_size("dvd.iso", 160 * 2048)

        run("bd-generate", "--input", "input image.png", "--output", "bd.iso", "--total-sectors", "1024")
        check_size("bd.iso", 1024 * 2048)


if __name__ == "__main__":
    check_package(Path(sys.argv[1]).resolve(strict=True))
