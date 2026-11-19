#!/usr/bin/env python3
"""Render assets/mascot.grid (the Wrangl groundhog, one character per pixel) to assets/wrangl.svg, herdr-desktop/mods/ah2/wrangl-color.svg, web/ui/wrangl.svg,
assets/mascot.ansi (24-bit colour, half-block rows) and assets/mascot.txt (no colour).
Run from the plugin root: python3 scripts/gen-mascot.py"""
import pathlib
root = pathlib.Path(__file__).resolve().parent.parent / "assets"
grid = (root / "mascot.grid").read_text().split()
assert len(grid) % 2 == 0 and len({len(r) for r in grid}) == 1, "grid must be rectangular with an even row count"
COL = {"H": "#6B3A1E", "B": "#D2693C", "D": "#3B1F14", "W": "#FFFFFF"}
w, h = len(grid[0]), len(grid)
rects = "".join(f'<rect x="{x}" y="{y}" width="1" height="1" fill="{COL[c]}"/>'
                for y, row in enumerate(grid) for x, c in enumerate(row) if c != ".")
(root / "wrangl.svg").write_text(
    f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="-1 -1 {w + 2} {h + 2}" shape-rendering="crispEdges">'
    f'<title>Wrangl</title>{rects}</svg>\n')
# The herdr mod's copy: the same colour file, drawn with image(), which keeps a file's own colours (svg() tints to one).
(root.parent / "herdr-desktop" / "mods" / "ah2" / "wrangl-color.svg").write_text((root / "wrangl.svg").read_text())
# The web UI's favicon: served from web/ui like its other static files (the page's CSP refuses a data: icon).
(root.parent / "web" / "ui" / "wrangl.svg").write_text((root / "wrangl.svg").read_text())
rgb = lambda c: ";".join(str(int(COL[c][i:i + 2], 16)) for i in (1, 3, 5))
ansi, plain = [], []
for y in range(0, h, 2):
    a = p = ""
    for top, bot in zip(grid[y], grid[y + 1]):
        if top == "." and bot == ".": a += "\x1b[0m "; p += " "
        elif bot == ".": a += f"\x1b[0;38;2;{rgb(top)}m▀"; p += "▀"
        elif top == ".": a += f"\x1b[0;38;2;{rgb(bot)}m▄"; p += "▄"
        else: a += f"\x1b[38;2;{rgb(top)};48;2;{rgb(bot)}m▀"; p += "█" if top == bot else "▓"
    ansi.append(a + "\x1b[0m"); plain.append(p.rstrip())
(root / "mascot.ansi").write_text("\n".join(ansi) + "\n")
(root / "mascot.txt").write_text("\n".join(plain) + "\n")
