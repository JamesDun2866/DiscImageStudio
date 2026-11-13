"""Generate the C# CD EFM (8-to-14) table from ECMA-130 Annex D, Table D.1.

Usage:
  python tools/generate_cd_efm_table.py ECMA-130.pdf output.cs

The table is extracted twice, with poppler's `pdftotext -layout` and with pdfplumber,
and both extractions must agree. Each must contain all 256 bytes exactly once with 256
distinct 14-bit words that obey the CD run-length rule (2..10 ZEROs between ONEs, at most
8 leading and 8 trailing ZEROs inside a word; Annex D/E). The source PDF used for the
committed table is the 2nd edition (June 1996):
https://ecma-international.org/wp-content/uploads/ECMA-130_2nd_edition_june_1996.pdf
"""

from __future__ import annotations

import hashlib
import re
import subprocess
import sys
from pathlib import Path

import pdfplumber

ROW = re.compile(r"\b([01]{8})\s+([01]{14})\b")


def annex_d_text(full_text: str) -> str:
    start = re.search(r"^\s*Annex D\s*$", full_text, re.MULTILINE)
    end = re.search(r"^\s*Annex E\s*$", full_text, re.MULTILINE)
    if start is None or end is None or end.start() <= start.start():
        raise RuntimeError("could not locate Annex D in the extracted text")
    # The table of contents also names the annexes; the table itself follows the heading.
    body = full_text[start.end():end.start()]
    if "Table D.1" not in body:
        raise RuntimeError("Annex D text does not contain Table D.1")
    return body


def parse(text: str) -> dict[int, str]:
    table: dict[int, str] = {}
    for byte_bits, word in ROW.findall(text):
        value = int(byte_bits, 2)
        if value in table and table[value] != word:
            raise RuntimeError(f"byte {value:#04x} listed twice with different words")
        table[value] = word
    return table


def extract_pdftotext(pdf_path: Path) -> dict[int, str]:
    text = subprocess.run(["pdftotext", "-layout", str(pdf_path), "-"],
                          check=True, capture_output=True, text=True).stdout
    return parse(annex_d_text(text))


def extract_pdfplumber(pdf_path: Path) -> dict[int, str]:
    with pdfplumber.open(pdf_path) as pdf:
        text = "\n".join(page.extract_text() or "" for page in pdf.pages)
    return parse(annex_d_text(text))


def validate(table: dict[int, str]) -> None:
    if sorted(table) != list(range(256)):
        raise RuntimeError("table does not contain bytes 0..255 exactly once")
    if len(set(table.values())) != 256:
        raise RuntimeError("table words are not distinct")
    for value, word in table.items():
        ones = [index for index, bit in enumerate(word) if bit == "1"]
        if not ones:
            raise RuntimeError(f"word for {value:#04x} has no ONE")
        if ones[0] > 8 or 13 - ones[-1] > 8:
            raise RuntimeError(f"word for {value:#04x} has more than 8 boundary ZEROs")
        for first, second in zip(ones, ones[1:]):
            if not 2 <= second - first - 1 <= 10:
                raise RuntimeError(f"word for {value:#04x} violates the 2..10 ZERO rule")


def generate(pdf_path: Path) -> str:
    first = extract_pdftotext(pdf_path)
    second = extract_pdfplumber(pdf_path)
    if first != second:
        differing = sorted(value for value in range(256) if first.get(value) != second.get(value))
        raise RuntimeError(f"pdftotext and pdfplumber disagree for bytes {differing[:16]}")
    validate(first)
    digest = hashlib.sha256(pdf_path.read_bytes()).hexdigest()
    words = [f"0x{int(first[value], 2):04X}" for value in range(256)]
    lines = []
    for offset in range(0, 256, 8):
        lines.append("        " + ", ".join(words[offset:offset + 8]) + ",")
    return (
        "// Generated from ECMA-130 2nd edition (June 1996) Annex D, Table D.1 by\n"
        "// tools/generate_cd_efm_table.py. Do not edit by hand.\n"
        f"// Source PDF SHA-256: {digest}\n"
        "namespace DiscImageStudio.Cd.Physical;\n\n"
        "internal static partial class CdEfmTable\n{\n"
        "    /// <summary>14-bit EFM channel word of every byte value. Bit 13 is the most significant\n"
        "    /// Channel bit, which is sent first.</summary>\n"
        "    private static readonly ushort[] WordsTable =\n"
        "    [\n" + "\n".join(lines) + "\n    ];\n"
        "}\n"
    )


def main() -> None:
    if len(sys.argv) != 3:
        raise SystemExit("usage: generate_cd_efm_table.py ECMA-130.pdf output.cs")
    pdf_path = Path(sys.argv[1])
    output_path = Path(sys.argv[2])
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(generate(pdf_path), encoding="utf-8", newline="\n")


if __name__ == "__main__":
    main()
