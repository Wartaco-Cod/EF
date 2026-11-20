# Clean-room rules

Taco Factory is a proprietary engine written from scratch. These rules keep it that way.

1. **No copied code.** No source, decompiled output, function, opcode table, script, packet layout or data file from any other game goes into this repository, even as a reference comment.
2. **Specs, not code, cross over.** Knowledge from earlier projects is written down as plain behaviour specs in `docs/specs/` ("the server owns loot rolls; the client predicts movement"). Engine code is written against those specs only.
3. **Separate roles when possible.** Whoever has worked inside another game's decompiled code writes specs. Engine code is written from the specs.
4. **No legacy names or assets.** Names, places, items, classes, UI frames, sounds, fonts and art from other games never appear here. Every asset comes from the Meshy pipeline or is made new (the shipped art is modelled from primitives by our own Blender scripts in `tools/art/blender/`; no third-party models, textures or reference images).
5. **The build enforces it.** `tools/banned_terms.py` scans every file name and text file. Any hit fails `build.bat` and `build.sh`. Add terms to `tools/banned_terms.txt`; never weaken the check to get a build through.
6. **Keep a record.** Each phase adds a dated entry to `docs/CLEAN_ROOM_LOG.md` saying what was written and from which spec. That log is what an IP attorney reviews before release.
