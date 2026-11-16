// Colour arithmetic shared by the app (look contrast, ah-ui-looks §4) and the tests' contrast and distinctness gates. sRGB 0-255 triples.
// Plain arithmetic, no dependency.

export const toLinear = (c) => (c / 255 <= 0.04045 ? c / 255 / 12.92 : ((c / 255 + 0.055) / 1.055) ** 2.4);

export const luminance = (rgb) => {
  const [r, g, b] = rgb.map(toLinear);
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
};

export const contrast = (a, b) => {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
};

// fg at alpha a over bg, in sRGB (how the browser composites).
export const over = (fg, bg, a) => fg.map((c, i) => c * a + bg[i] * (1 - a));

export const hex = (c) => [1, 3, 5].map((i) => parseInt(c.slice(i, i + 2), 16));
