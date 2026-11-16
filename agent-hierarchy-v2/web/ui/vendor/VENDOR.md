# Vendored libraries

Plain files copied from the npm registry tarballs below. They are not edited, and `tests/web/static.test.mjs` checks every file here against `SHA256SUMS`. To upgrade, replace the file, update the version and hashes here and in `SHA256SUMS`, and run `tests/web/run.sh`.

| File | Package | Version | Licence | Copied from the tarball's | Tarball sha256 |
|---|---|---|---|---|---|
| `preact.js` | preact | 10.29.8 | MIT (`LICENSE-preact.txt`) | `dist/preact.module.js` | `b18cb0a457f3d43c7bb30391a74ade7d13e03bc6e77915e061c70c0fe1123299` |
| `htm.js` | htm | 3.1.1 | Apache-2.0 (`LICENSE-htm.txt`) | `dist/htm.module.js` | `2425b9bee11409177bcabc7f32e319926fc6690c1701c0b257c88bdff2d5ba90` |
| `d3.min.js` | d3 | 7.9.0 | ISC (`LICENSE-d3.txt`) | `dist/d3.min.js` | `7e36605710a2ba54846797c8c6d888911341b215ae53257dc32a49a0b824355e` |
| `../fonts/jetbrains-mono-latin-wght-normal.woff2` | @fontsource-variable/jetbrains-mono | 5.3.0 | SIL Open Font License 1.1 (`../fonts/OFL-JetBrainsMono.txt`) | `files/jetbrains-mono-latin-wght-normal.woff2` | `996fe6368a480c9ce15d4de22a2682b7c40b403718fee2b15e7272f244fd993f` |

The tarballs are `https://registry.npmjs.org/preact/-/preact-10.29.8.tgz`, `https://registry.npmjs.org/htm/-/htm-3.1.1.tgz`, `https://registry.npmjs.org/d3/-/d3-7.9.0.tgz`, and `https://registry.npmjs.org/@fontsource-variable/jetbrains-mono/-/jetbrains-mono-5.3.0.tgz`. The font file and its licence (the package's `LICENSE`) are served from `web/ui/fonts/`, and `SHA256SUMS` names them as `../fonts/<file>`.

`preact-render-to-string` 6.7.0 (MIT, tarball sha256 `fc7506c7d8bfaa298d51f4b5be1a0972a54b8cdac2bcde6b400131fdf40be2e5`, `dist/index.mjs`) is used only by the tests and lives in `tests/web/vendor/`. It is not served. Its one edit from the tarball is the import of `preact`, written as the relative path of the Preact the page ships, so the tests share one copy and need no loader.

This directory is exempt from the banned-API scan in the tests (Preact itself contains `innerHTML` for the escape hatch the app never uses). Only the pinned files above may live here.
